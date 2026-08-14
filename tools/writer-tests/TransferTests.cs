using System;
using PIXMYD_Nav.Core.Json;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.Transfer;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The pairing ticket and the transfer manifest, per docs/contracts/transfer.md.
    ///
    /// The QR fixtures are segno's exact matrix output, captured offline via
    ///   segno.make(payload, error='M', version=3, mask=M, boost_error=False, mode='byte')
    /// at the version and mask QrEncoder itself chose. Same reasoning as
    /// QrEncoderTests: a round trip through a decoder that shares the encoder's
    /// assumptions proves nothing, and this is the payload that will actually be
    /// on a monitor with a phone pointed at it.
    /// </summary>
    internal static class TransferTests
    {
        public static int Run()
        {
            int failures = 0;

            TicketRoundTrip(ref failures);
            PayloadIsAlwaysThirtyNineBytes(ref failures);
            MalformedTicketsRefused(ref failures);
            QrFixtures(ref failures);
            NameSafety(ref failures);
            SessionJson(ref failures);

            return failures;
        }

        // MARK: - Ticket

        private static void TicketRoundTrip(ref int failures)
        {
            var ticket = new TransferTicket("192.168.1.100", 48080, "0123456789abcdef");

            // 192.168.1.100 -> c0.a8.01.64, and 48080 -> 0xbbd0.
            Program.Check(ticket.EndpointHex == "c0a80164bbd0",
                "endpoint packs to hex, got " + ticket.EndpointHex, ref failures);
            Program.Check(ticket.Payload == "pixmy://t/c0a80164bbd0/0123456789abcdef",
                "payload format, got " + ticket.Payload, ref failures);

            TransferTicket back = TransferTicket.Parse(ticket.Payload);
            Program.Check(back != null, "payload parses back", ref failures);
            if (back == null) return;
            Program.Check(back.Host == "192.168.1.100", "host survives the round trip", ref failures);
            Program.Check(back.Port == 48080, "port survives the round trip", ref failures);
            Program.Check(back.Token == "0123456789abcdef", "token survives the round trip", ref failures);
        }

        /// <summary>
        /// The reason the payload is packed hex rather than a readable URL.
        /// QrEncoder is versions 1-3 only -- 42 bytes at level M -- and throws
        /// rather than emit a malformed symbol. A dotted-quad and a decimal port
        /// would vary in length with the address, so a session that encodes on
        /// one machine would fail on the next.
        /// </summary>
        private static void PayloadIsAlwaysThirtyNineBytes(ref int failures)
        {
            var hosts = new string[] { "0.0.0.0", "10.0.0.1", "192.168.1.100", "255.255.255.255", "172.16.254.3" };
            var ports = new int[] { 1, 80, 48080, 65535, 8080 };

            for (int i = 0; i < hosts.Length; i++)
            {
                var ticket = new TransferTicket(hosts[i], ports[i], "deadbeefcafef00d");
                Program.Check(ticket.Payload.Length == TransferTicket.PayloadLength,
                    "payload for " + hosts[i] + ":" + ports[i] + " is " + ticket.Payload.Length +
                    " bytes, expected 39", ref failures);

                // And it has to actually encode, which is the thing that breaks.
                QrCode qr = QrEncoder.Encode(ticket.Payload);
                Program.Check(qr.Version <= 3, "encodes within the encoder ceiling", ref failures);
            }
        }

        private static void MalformedTicketsRefused(ref int failures)
        {
            Refused("192.168.1", 48080, "0123456789abcdef", "three octets", ref failures);
            Refused("192.168.1.256", 48080, "0123456789abcdef", "octet out of range", ref failures);
            Refused("192.168.01.100", 48080, "0123456789abcdef", "leading zero is an octal literal", ref failures);
            Refused("192.168.1.100", 0, "0123456789abcdef", "port 0", ref failures);
            Refused("192.168.1.100", 70000, "0123456789abcdef", "port out of range", ref failures);
            Refused("192.168.1.100", 48080, "0123abcd", "short token", ref failures);
            Refused("192.168.1.100", 48080, "zzzzzzzzzzzzzzzz", "non-hex token", ref failures);

            Program.Check(TransferTicket.Parse("pixmy://t/c0a80164bb/0123456789abcdef") == null,
                "short endpoint field is refused", ref failures);
            Program.Check(TransferTicket.Parse("pixmy://p/b7f3c2e1/P001") == null,
                "a marker payload is not a ticket", ref failures);
            Program.Check(TransferTicket.Parse("") == null, "empty payload is refused", ref failures);

            // A token is 64 bits from the OS RNG, and two calls must not match.
            string a = TransferTicket.NewToken();
            string b = TransferTicket.NewToken();
            Program.Check(TransferTicket.IsToken(a), "generated token is 16 hex characters", ref failures);
            Program.Check(a != b, "generated tokens differ", ref failures);
        }

        private static void Refused(string host, int port, string token, string why, ref int failures)
        {
            bool threw = false;
            try { new TransferTicket(host, port, token); }
            catch (ArgumentException) { threw = true; }
            Program.Check(threw, "refused: " + why, ref failures);
        }

        // MARK: - QR

        private static void QrFixtures(ref int failures)
        {
            CheckFixture("pixmy://t/c0a80164bbd0/0123456789abcdef", 3, 6, TransferV3Mask6, ref failures);
            CheckFixture("pixmy://t/0a000001001f/deadbeefcafef00d", 3, 3, TransferV3Mask3, ref failures);
            CheckFixture("pixmy://t/ffffffffffff/00000000ffffffff", 3, 2, TransferV3Mask2, ref failures);

            // The bitmap the transfer window shows. Four modules of quiet zone
            // is not decoration: a symbol drawn hard against a dark panel is the
            // most common reason a code that looks right will not scan.
            QrCode qr = QrEncoder.Encode("pixmy://t/c0a80164bbd0/0123456789abcdef");
            byte[] bmp = QrRender.ToBmp(qr, 6);
            int side = (qr.Size + QrRender.QuietZoneModules * 2) * 6;
            Program.Check(bmp[0] == (byte)'B' && bmp[1] == (byte)'M', "bitmap has a BMP header", ref failures);
            Program.Check(ReadInt32(bmp, 18) == side, "bitmap width covers the quiet zone", ref failures);
            Program.Check(ReadInt32(bmp, 22) == side, "bitmap height covers the quiet zone", ref failures);
            Program.Check(IsWhite(bmp, side, 1, 1), "the quiet zone is light", ref failures);
        }

        private static void CheckFixture(string payload, int version, int mask, string[] expected, ref int failures)
        {
            QrCode qr = QrEncoder.Encode(payload);
            Program.Check(qr.Version == version,
                payload + ": version " + qr.Version + ", expected " + version, ref failures);
            Program.Check(qr.MaskPattern == mask,
                payload + ": mask " + qr.MaskPattern + ", expected " + mask, ref failures);
            if (qr.Version != version || qr.MaskPattern != mask) return;

            int differences = 0;
            for (int r = 0; r < qr.Size; r++)
                for (int c = 0; c < qr.Size; c++)
                    if (qr.Modules[r, c] != (expected[r][c] == '1')) differences++;

            Program.Check(differences == 0,
                payload + ": " + differences + " module(s) differ from segno", ref failures);
        }

        private static bool IsWhite(byte[] bmp, int side, int x, int y)
        {
            int stride = side * 3;
            stride += (4 - stride % 4) % 4;
            int offset = 54 + (side - 1 - y) * stride + x * 3;
            return bmp[offset] == 0xFF && bmp[offset + 1] == 0xFF && bmp[offset + 2] == 0xFF;
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24;
        }

        // MARK: - Manifest

        private static void NameSafety(ref int failures)
        {
            var safe = new string[] { "points.json", "P001_photo.png", "images/P001.png", "a/b/c.glb" };
            foreach (string name in safe)
                Program.Check(TransferManifest.IsSafeName(name), "safe: " + name, ref failures);

            var refusedNames = new string[]
            {
                "", "..", "../escape.txt", "a/../../b", "/etc/passwd", "C:/windows/system32/x",
                "a\\b", "a//b", "trailing.", "trailing ", "with\u0001control"
            };
            foreach (string name in refusedNames)
                Program.Check(!TransferManifest.IsSafeName(name), "refused: '" + name + "'", ref failures);
        }

        private static void SessionJson(ref int failures)
        {
            var offer = new TransferOffer();
            offer.Name = "L01 Column Marks";
            offer.Kind = "points";
            offer.Files.Add(new TransferFileEntry("points.json", 4821));
            offer.Files.Add(new TransferFileEntry("P001_photo.png", 184203));

            string json = TransferManifest.Session(
                "3f2a9c1e-0000-0000-0000-000000000000",
                "WS-BIM-04",
                "TowerA.nwd",
                new DateTime(2026, 8, 13, 15, 2, 11, DateTimeKind.Utc),
                offer, true, 268435456);

            // contractVersion is the first field: the guest probes it before it
            // commits to the rest of the schema.
            Program.Check(json.StartsWith("{\"contractVersion\":\"1.0\"", StringComparison.Ordinal),
                "contractVersion is first, got " + json.Substring(0, Math.Min(40, json.Length)), ref failures);

            // And it has to parse, by the reader that reads every other contract.
            JsonValue root = JsonReader.Parse(json);
            Program.Check(root["sessionId"].AsString("") == "3f2a9c1e-0000-0000-0000-000000000000",
                "sessionId survives", ref failures);
            Program.Check(root["host"].AsString("") == "WS-BIM-04", "host survives", ref failures);
            Program.Check(root["expiresUtc"].AsString("") == "2026-08-13T15:02:11.000Z",
                "expiry is ISO 8601, got " + root["expiresUtc"].AsString(""), ref failures);
            Program.Check(root["download"]["files"].Count == 2, "both files are offered", ref failures);
            Program.Check(root["download"]["files"].At(1)["bytes"].AsNumber(0) == 184203,
                "byte counts survive", ref failures);
            Program.Check(root["upload"]["accepted"].AsBool(false), "upload is accepted", ref failures);

            // An upload-only session writes download: null, and that is valid.
            string uploadOnly = TransferManifest.Session(
                "s", "host", "doc", DateTime.UtcNow, null, true, 1024);
            JsonValue uploadRoot = JsonReader.Parse(uploadOnly);
            Program.Check(uploadRoot["download"].IsNull, "an upload-only session offers nothing", ref failures);

            string commit = TransferManifest.CommitResult(true, "44e0b8a2", "Received.");
            JsonValue commitRoot = JsonReader.Parse(commit);
            Program.Check(commitRoot["accepted"].AsBool(false), "commit accepted", ref failures);
            Program.Check(commitRoot["captureId"].AsString("") == "44e0b8a2", "commit carries the id", ref failures);

            string refused = TransferManifest.CommitResult(false, null, "No capture.json arrived.");
            JsonValue refusedRoot = JsonReader.Parse(refused);
            Program.Check(!refusedRoot["accepted"].AsBool(true), "commit refused", ref failures);
            Program.Check(refusedRoot["message"].AsString("").Length > 0, "a refusal carries a message", ref failures);
        }

        // MARK: - segno fixtures
        private static readonly string[] TransferV3Mask6 = {
            "11111110100111100010101111111",
            "10000010100011001101101000001",
            "10111010110001001001001011101",
            "10111010000101001110001011101",
            "10111010101000101101101011101",
            "10000010000100101110001000001",
            "11111110101010101010101111111",
            "00000000001011010011000000000",
            "10011111101010111111010010111",
            "00000001011000110001000111000",
            "01011011101111011110110111100",
            "01101100000100001101011111001",
            "10010010010001111011111000010",
            "10010001011001111000011011011",
            "01000110100011010111010100001",
            "10000100010010101011101010111",
            "00100010101010101010110101011",
            "11111101000101011101110010100",
            "11010111101010011011111001001",
            "11110101101000100001000101100",
            "11101010011011100111111111111",
            "00000000101000111001100011010",
            "11111110100110111101101011000",
            "10000010110000110110100010010",
            "10111010100111110011111111001",
            "10111010101111010001000000001",
            "10111010001001011000000010111",
            "10000010011101010001001011101",
            "11111110111100011010100011000"
        };

        private static readonly string[] TransferV3Mask3 = {
            "11111110111110100111001111111",
            "10000010111010100010101000001",
            "10111010011001010010101011101",
            "10111010100111010100001011101",
            "10111010000110011101001011101",
            "10000010010101000011101000001",
            "11111110101010101010101111111",
            "00000000100110101100100000000",
            "10110111010000101111101001011",
            "10110100100100001011001011111",
            "01100110011100000110100001110",
            "10111001011011010011001000001",
            "01010111010110010100110001111",
            "11111100101100111101111000011",
            "00001011110101000001001000011",
            "01000000011010010000010010000",
            "01110110101100100010000011001",
            "00010001101100010000010001100",
            "10010111000011111000110100100",
            "00000101110001110100100010100",
            "01110011000011101111111111101",
            "00000000101100001011100011101",
            "11111110100110100001101011010",
            "10000010110111110001100011010",
            "10111010000010010100111110100",
            "10111010110110011100100111001",
            "10111010101100100011010000101",
            "10000010000001111001110011010",
            "11111110110111000010100001010"
        };

        private static readonly string[] TransferV3Mask2 = {
            "11111110011110001111001111111",
            "10000010010101110100001000001",
            "10111010101010001001101011101",
            "10111010111001110110001011101",
            "10111010100010001001101011101",
            "10000010110111110010001000001",
            "11111110101010101010101111111",
            "00000000110010001011100000000",
            "10111110001101110110001111100",
            "00000101000110001001111111111",
            "00000010011111110010000001000",
            "01011000111000001011111111010",
            "11010111001000010110000001111",
            "00001001011110001001111110101",
            "11101011100010010010000001000",
            "10010001001110001011111110000",
            "11111110101100010110000001111",
            "11111001001111001001111110111",
            "10101011011101110010000100100",
            "10110001111101001011111100010",
            "10000110010110110110111110110",
            "00000000100000001000100011101",
            "11111110011010010011101011100",
            "10000010110110110011100010001",
            "10111010100100011110111110100",
            "10111010110100100000100001111",
            "10111010110001110011011011110",
            "10000010010011110010100011010",
            "11111110101011111111011111100"
        };
    }
}
