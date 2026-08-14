using System;
using System.Globalization;
using System.Security.Cryptography;

namespace PIXMYD_Nav.Core.Transfer
{
    /// <summary>
    /// The pairing ticket encoded into the on-screen QR code: where the transfer
    /// session is, and the secret that opens it. See docs/contracts/transfer.md.
    ///
    /// The payload is packed to a fixed 39 bytes and can never drift:
    ///
    ///   "pixmy://t/"  10
    ///   host           8   IPv4 as hex, two characters per octet
    ///   port           4   hex
    ///   "/"            1
    ///   token         16   64 bits of hex
    ///
    /// The fixed width is the whole point. QrEncoder is byte mode, level M,
    /// versions 1-3 only -- a hard ceiling of 42 bytes, and it throws rather than
    /// emit a malformed symbol. A dotted-quad and a decimal port would run to 40
    /// bytes on one machine and 34 on the next, so a session would encode on a
    /// developer's laptop and fail on a site workstation whose address happened to
    /// be longer. Hex removes the variable.
    ///
    /// Pure: no Navisworks, no sockets. In WriterTests.csproj.
    /// </summary>
    public sealed class TransferTicket
    {
        public const string Scheme = "pixmy";
        /// <summary>Every ticket encodes to exactly this many bytes.</summary>
        public const int PayloadLength = 39;

        public string Host;
        public int Port;
        /// <summary>16 lowercase hex characters.</summary>
        public string Token;

        public TransferTicket(string host, int port, string token)
        {
            if (!IsIPv4(host))
                throw new ArgumentException("Ticket host must be a dotted-quad IPv4 address: " + host);
            if (port < 1 || port > 65535)
                throw new ArgumentException("Ticket port out of range: " + port);
            if (!IsToken(token))
                throw new ArgumentException("Ticket token must be 16 hex characters.");

            Host = host;
            Port = port;
            Token = token.ToLowerInvariant();
        }

        /// <summary>
        /// A fresh 64-bit token from the OS cryptographic RNG.
        ///
        /// Never derived from the set id, the document name, or the clock. A token
        /// that can be guessed from something printed on a marker is not a token.
        /// </summary>
        public static string NewToken()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var chars = new char[16];
            for (int i = 0; i < 8; i++)
            {
                chars[i * 2] = HexDigit(bytes[i] >> 4);
                chars[i * 2 + 1] = HexDigit(bytes[i] & 0xF);
            }
            return new string(chars);
        }

        /// <summary>The packed 12-hex endpoint field.</summary>
        public string EndpointHex
        {
            get
            {
                string[] parts = Host.Split('.');
                return byte.Parse(parts[0], CultureInfo.InvariantCulture).ToString("x2", CultureInfo.InvariantCulture)
                     + byte.Parse(parts[1], CultureInfo.InvariantCulture).ToString("x2", CultureInfo.InvariantCulture)
                     + byte.Parse(parts[2], CultureInfo.InvariantCulture).ToString("x2", CultureInfo.InvariantCulture)
                     + byte.Parse(parts[3], CultureInfo.InvariantCulture).ToString("x2", CultureInfo.InvariantCulture)
                     + Port.ToString("x4", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The exact string encoded into the QR.</summary>
        public string Payload
        {
            get { return Scheme + "://t/" + EndpointHex + "/" + Token; }
        }

        /// <summary>
        /// Parse a payload back. Used by the tests to assert the round trip, and by
        /// nothing else -- the plugin produces tickets, the phone consumes them.
        /// </summary>
        public static TransferTicket Parse(string payload)
        {
            if (payload == null) return null;
            string prefix = Scheme + "://t/";
            if (!payload.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            string body = payload.Substring(prefix.Length);
            int slash = body.IndexOf('/');
            if (slash != 12) return null;

            string hex = body.Substring(0, 12).ToLowerInvariant();
            string token = body.Substring(slash + 1);
            if (!IsHex(hex) || !IsToken(token)) return null;

            int a = Convert.ToInt32(hex.Substring(0, 2), 16);
            int b = Convert.ToInt32(hex.Substring(2, 2), 16);
            int c = Convert.ToInt32(hex.Substring(4, 2), 16);
            int d = Convert.ToInt32(hex.Substring(6, 2), 16);
            int port = Convert.ToInt32(hex.Substring(8, 4), 16);
            if (port < 1) return null;

            return new TransferTicket(a + "." + b + "." + c + "." + d, port, token);
        }

        public static bool IsIPv4(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string[] parts = host.Split('.');
            if (parts.Length != 4) return false;
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                // A leading zero means someone is passing an octal literal, and
                // the two ends have to agree byte for byte.
                if (part.Length > 1 && part[0] == '0') return false;
                int value;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
                if (value < 0 || value > 255) return false;
            }
            return true;
        }

        public static bool IsToken(string token)
        {
            return token != null && token.Length == 16 && IsHex(token);
        }

        private static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static char HexDigit(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }
    }
}
