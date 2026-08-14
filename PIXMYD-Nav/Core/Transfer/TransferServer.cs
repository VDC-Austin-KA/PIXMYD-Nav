using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PIXMYD_Nav.Core.Transfer
{
    /// <summary>
    /// The local-network half of docs/contracts/transfer.md: a small HTTP server
    /// that serves one folder and accepts one capture, for as long as the user
    /// has the transfer window open.
    ///
    /// ## Why TcpListener and not HttpListener
    ///
    /// HttpListener is the obvious choice and it is the wrong one here. Binding
    /// any prefix other than loopback requires a URL ACL reservation, which means
    /// running `netsh http add urlacl` as an administrator. A BIM coordinator
    /// cannot do that on a locked-down site workstation, and a feature that needs
    /// an elevated prompt before it works once is a feature nobody uses. A TCP
    /// socket needs no reservation.
    ///
    /// The cost is parsing HTTP by hand, and it is small because the contract is
    /// four endpoints with no ranges, no chunked encoding, no keep-alive and no
    /// compression. Anything outside that subset gets a 400 rather than a guess.
    ///
    /// ## What bounds it
    ///
    /// Started by a button, never at plugin load. Bound to one real LAN address,
    /// never 0.0.0.0. Expires. One 64-bit token from the OS RNG. Ten failed
    /// authentications end the session. It serves exactly one directory and
    /// writes to exactly one inbox, and every request name is checked by
    /// TransferManifest.IsSafeName before it becomes a path.
    ///
    /// Expiry is enforced when a request arrives rather than by a timer, per
    /// RULES.md section 3 -- there are no background pollers here, only a thread
    /// blocked on accept().
    /// </summary>
    public sealed class TransferServer : IDisposable
    {
        private const int BacklogLimit = 8;
        private const int MaxHeaderBytes = 8 * 1024;
        private const int MaxRequestBodyBytes = 512 * 1024 * 1024;
        private const long MaxDrainBytes = 8L * 1024 * 1024;

        private readonly object _gate = new object();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private int _failedAuthentications;
        private DateTime _expiresUtc;

        private readonly string _offerDirectory;
        private readonly string _inboxDirectory;
        private readonly TransferOffer _offer;
        private readonly bool _acceptsUpload;
        private readonly long _maxUploadBytes;
        private readonly string _hostName;
        private readonly string _documentName;
        private readonly string _sessionId;

        /// <summary>Raised on the listener thread. Marshal before touching UI.</summary>
        public event Action<string> Activity;
        /// <summary>Raised with the inbox path when a guest commits a capture.</summary>
        public event Action<string> CaptureCommitted;

        public TransferTicket Ticket { get; private set; }
        public bool IsRunning { get { return _running; } }
        public DateTime ExpiresUtc { get { return _expiresUtc; } }

        public TransferServer(
            TransferOffer offer,
            string offerDirectory,
            string inboxDirectory,
            bool acceptsUpload,
            long maxUploadBytes,
            string hostName,
            string documentName)
        {
            _offer = offer;
            _offerDirectory = offerDirectory;
            _inboxDirectory = inboxDirectory;
            _acceptsUpload = acceptsUpload;
            _maxUploadBytes = maxUploadBytes;
            _hostName = hostName ?? "";
            _documentName = documentName ?? "";
            _sessionId = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Bind and start. Returns the ticket to put in the QR code.
        /// </summary>
        /// <param name="lifetime">How long the session stays open without activity.</param>
        public TransferTicket Start(TimeSpan lifetime)
        {
            if (_running) return Ticket;

            IPAddress address = LocalIPv4();
            if (address == null)
                throw new InvalidOperationException(
                    "This machine has no IPv4 address on a local network, so a transfer code cannot be made. " +
                    "Connect to the site network, or export to a folder instead.");

            // Port 0 lets the OS pick a free one. A fixed port would collide with
            // whatever else is on a shared workstation, and the port travels in
            // the QR anyway.
            _listener = new TcpListener(address, 0);
            _listener.Start(BacklogLimit);

            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Ticket = new TransferTicket(address.ToString(), port, TransferTicket.NewToken());
            _expiresUtc = DateTime.UtcNow.Add(lifetime);
            _failedAuthentications = 0;
            _running = true;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "PIXMYD-Nav transfer";
            _thread.Start();

            Report("Listening on " + Ticket.Host + ":" + Ticket.Port);
            return Ticket;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            Report("Transfer session closed.");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// The first non-loopback IPv4 on an operational interface.
        ///
        /// A workstation commonly has several -- a wired LAN, a wifi adaptor, and
        /// a pile of virtual ones from Hyper-V or VPN clients. Preferring the one
        /// with a default gateway picks the network the phone is actually on
        /// rather than a 172.x virtual switch nothing can reach.
        /// </summary>
        public static IPAddress LocalIPv4()
        {
            IPAddress fallback = null;
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface adapter in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    System.Net.NetworkInformation.IPInterfaceProperties properties = adapter.GetIPProperties();
                    bool hasGateway = properties.GatewayAddresses != null && properties.GatewayAddresses.Count > 0;

                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(info.Address)) continue;
                        if (hasGateway) return info.Address;
                        if (fallback == null) fallback = info.Address;
                    }
                }
            }
            catch
            {
                // A machine whose adaptors cannot be enumerated has no transfer
                // path; the caller reports that rather than throwing here.
            }
            return fallback;
        }

        // MARK: - Loop

        private void Loop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    TcpListener listener = _listener;
                    if (listener == null) break;
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    // Stop() closes the listener out from under accept(), which
                    // is the normal way this loop ends.
                    break;
                }

                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 60000;
                    using (NetworkStream stream = client.GetStream())
                        Handle(stream);
                }
                catch (Exception e)
                {
                    Report("Request failed: " + e.Message);
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private void Handle(NetworkStream stream)
        {
            string method, target;
            Dictionary<string, string> headers;
            if (!ReadRequestHead(stream, out method, out target, out headers))
            {
                Write(stream, 400, "text/plain", Encoding.UTF8.GetBytes("Bad request"));
                return;
            }

            if (DateTime.UtcNow > _expiresUtc)
            {
                Refuse(stream, headers, 401, null);
                Report("Refused an expired request.");
                Stop();
                return;
            }

            string token = null;
            string authorization;
            if (headers.TryGetValue("authorization", out authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authorization.Substring(7).Trim();
            }

            if (Ticket == null || !FixedTimeEquals(token, Ticket.Token))
            {
                // No hint about what the right token would be.
                Refuse(stream, headers, 401, null);
                if (Interlocked.Increment(ref _failedAuthentications) >= 10)
                {
                    Report("Too many bad codes. Session closed.");
                    Stop();
                }
                return;
            }

            // Activity refreshes the session, so a long download does not expire
            // halfway through.
            _expiresUtc = DateTime.UtcNow.AddMinutes(15);

            string path = target;
            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            path = Uri.UnescapeDataString(path);

            if (method == "GET" && path == "/session")
            {
                byte[] body = Encoding.UTF8.GetBytes(TransferManifest.Session(
                    _sessionId, _hostName, _documentName, _expiresUtc,
                    _offer, _acceptsUpload, _maxUploadBytes));
                Write(stream, 200, "application/json", body);
                Report("Guest opened the session.");
                return;
            }

            if (method == "GET" && path.StartsWith("/file/", StringComparison.Ordinal))
            {
                ServeFile(stream, path.Substring("/file/".Length));
                return;
            }

            if (method == "POST" && path == "/capture/commit")
            {
                Commit(stream, headers);
                return;
            }

            if (method == "POST" && path.StartsWith("/capture/", StringComparison.Ordinal))
            {
                ReceiveFile(stream, headers, path.Substring("/capture/".Length));
                return;
            }

            Refuse(stream, headers, 404, null);
        }

        private void ServeFile(NetworkStream stream, string name)
        {
            if (!TransferManifest.IsSafeName(name) || !IsOffered(name))
            {
                Write(stream, 404, "text/plain", new byte[0]);
                return;
            }

            string full = Path.Combine(_offerDirectory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!IsInside(_offerDirectory, full) || !File.Exists(full))
            {
                Write(stream, 404, "text/plain", new byte[0]);
                return;
            }

            byte[] body = File.ReadAllBytes(full);
            Write(stream, 200, "application/octet-stream", body);
            Report("Sent " + name + " (" + body.Length + " bytes)");
        }

        private void ReceiveFile(NetworkStream stream, Dictionary<string, string> headers, string name)
        {
            if (!_acceptsUpload)
            {
                Refuse(stream, headers, 403, null);
                return;
            }
            if (!TransferManifest.IsSafeName(name))
            {
                Refuse(stream, headers, 400, "Unsafe name");
                return;
            }

            long length = ContentLength(headers);
            if (length < 0 || length > MaxRequestBodyBytes || length > _maxUploadBytes)
            {
                Refuse(stream, headers, 413, null);
                return;
            }

            string full = Path.Combine(_inboxDirectory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!IsInside(_inboxDirectory, full))
            {
                Refuse(stream, headers, 400, "Unsafe name");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, wanted);
                    if (read <= 0) throw new IOException("The upload ended early.");
                    file.Write(buffer, 0, read);
                    remaining -= read;
                }
            }

            Write(stream, 201, "text/plain", new byte[0]);
            Report("Received " + name + " (" + length + " bytes)");
        }

        private void Commit(NetworkStream stream, Dictionary<string, string> headers)
        {
            DrainBody(stream, headers, MaxDrainBytes);

            string captureJson = Path.Combine(_inboxDirectory, "capture.json");
            if (!File.Exists(captureJson))
            {
                byte[] refusal = Encoding.UTF8.GetBytes(TransferManifest.CommitResult(
                    false, null, "No capture.json arrived, so there is nothing to place."));
                Write(stream, 200, "application/json", refusal);
                return;
            }

            // Parsed here so a guest gets a real answer, but nothing is placed.
            // The contract puts the accuracy decision at the workstation: a
            // person on a scaffold is the wrong one to judge whether a 40 mm RMS
            // is good enough for what this model is about to be used for.
            string captureId = null;
            string message;
            bool accepted;
            try
            {
                Capture.CaptureFile capture = Capture.CaptureReader.Read(File.ReadAllText(captureJson));
                captureId = capture.CaptureId;
                accepted = true;
                message = "Received. Review the fit in PIXMYD-Nav before placing it.";
            }
            catch (Exception e)
            {
                accepted = false;
                message = e.Message;
            }

            byte[] body = Encoding.UTF8.GetBytes(TransferManifest.CommitResult(accepted, captureId, message));
            Write(stream, 200, "application/json", body);

            if (accepted)
            {
                Report("A capture arrived and is waiting for review.");
                Action<string> handler = CaptureCommitted;
                if (handler != null) handler(_inboxDirectory);
            }
            else
            {
                Report("Rejected a capture: " + message);
            }
        }

        // MARK: - HTTP plumbing

        private static bool ReadRequestHead(
            NetworkStream stream,
            out string method,
            out string target,
            out Dictionary<string, string> headers)
        {
            method = null;
            target = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var head = new List<byte>(1024);
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) return false;
                head.Add((byte)b);
                if (head.Count > MaxHeaderBytes) return false;

                // Looking for CRLFCRLF.
                if ((matched == 0 || matched == 2) && b == '\r') matched++;
                else if ((matched == 1 || matched == 3) && b == '\n') matched++;
                else matched = b == '\r' ? 1 : 0;
            }

            string text = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            string[] request = lines[0].Split(' ');
            if (request.Length < 2) return false;
            method = request[0];
            target = request[1];

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) break;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return true;
        }

        private static long ContentLength(Dictionary<string, string> headers)
        {
            string value;
            if (!headers.TryGetValue("content-length", out value)) return 0;
            long length;
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length) ? length : -1;
        }

        /// <summary>
        /// Read and discard a request body.
        ///
        /// Not optional on the refusal paths. Writing a response and closing
        /// while the client is still sending makes Windows reset the connection,
        /// and the client sees a transport error instead of the 400 that
        /// explains what it did wrong. Found exactly that way: a rejected upload
        /// surfaced as "an existing connection was forcibly closed" rather than
        /// as the refusal the server had already written.
        ///
        /// Bounded, because a client that keeps sending after being told the
        /// body is too large is not the case to optimise for.
        /// </summary>
        private static void DrainBody(NetworkStream stream, Dictionary<string, string> headers, long cap)
        {
            long remaining = ContentLength(headers);
            if (remaining <= 0) return;
            if (remaining > cap) remaining = cap;

            var buffer = new byte[8192];
            while (remaining > 0)
            {
                int read;
                try { read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); }
                catch { break; }
                if (read <= 0) break;
                remaining -= read;
            }
        }

        /// <summary>Answer a request this server will not carry out, without
        /// leaving its body unread on the socket.</summary>
        private static void Refuse(
            NetworkStream stream,
            Dictionary<string, string> headers,
            int status,
            string message)
        {
            DrainBody(stream, headers, MaxDrainBytes);
            Write(stream, status, "text/plain", message == null ? new byte[0] : Encoding.UTF8.GetBytes(message));
        }

        private static void Write(NetworkStream stream, int status, string contentType, byte[] body)
        {
            string reason;
            switch (status)
            {
                case 200: reason = "OK"; break;
                case 201: reason = "Created"; break;
                case 400: reason = "Bad Request"; break;
                case 401: reason = "Unauthorized"; break;
                case 403: reason = "Forbidden"; break;
                case 404: reason = "Not Found"; break;
                case 413: reason = "Payload Too Large"; break;
                default: reason = "Error"; break;
            }

            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            // No keep-alive: one request per connection keeps the parser to the
            // subset above and costs nothing on a LAN.
            head.Append("Connection: close\r\n");
            head.Append("Cache-Control: no-store\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        // MARK: - Guards

        private bool IsOffered(string name)
        {
            if (_offer == null) return false;
            foreach (TransferFileEntry entry in _offer.Files)
                if (string.Equals(entry.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Belt and braces on top of IsSafeName: resolve both paths and check
        /// containment, so a name that slips through the textual check still
        /// cannot reach outside the served directory.
        /// </summary>
        private static bool IsInside(string root, string candidate)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root);
                if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    fullRoot += Path.DirectorySeparatorChar;
                string fullCandidate = Path.GetFullPath(candidate);
                return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Compares in time independent of how many characters match.
        ///
        /// Over a LAN the timing signal is buried in jitter, so this is not the
        /// control that matters -- the 64-bit token and the ten-strike limit are.
        /// It costs three lines not to have to think about it.
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        private void Report(string message)
        {
            Action<string> handler = Activity;
            if (handler != null) handler(message);
        }
    }
}
