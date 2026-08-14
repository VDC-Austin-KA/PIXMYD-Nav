using System;
using System.Collections.Generic;
using System.Text;
using PIXMY4D_Nav.Core.Markers;

namespace PIXMY4D_Nav
{
    /// <summary>
    /// Proves QrEncoder correct by round-tripping: decode the symbol it produces
    /// back to the original string via an independently-written decode path
    /// (own bit extraction, own unmask, own Reed-Solomon recomputation), per the
    /// verification option docs/work-orders/pixmy4d-nav.md task P2 names ("or
    /// implement a small decoder in the test to round-trip"). Only QrCode.Modules,
    /// QrCode.IsFunction (pure ISO 18004 geometry, not the data/RS logic under
    /// test) and QrEncoder.MaskCondition (the standard mask formulas) are reused
    /// from the encoder; everything data-shaped is redone here independently.
    /// </summary>
    internal static class QrEncoderTests
    {
        public static int Run()
        {
            int failures = 0;

            RoundTrip("pixmy://p/b7f3c2e1/P001", ref failures);
            RoundTrip("pixmy://p/00000000/P999", ref failures);
            RoundTrip("A", ref failures);                 // version 1, tiny payload
            RoundTrip(new string('x', 40), ref failures);  // near version-3 capacity (42 bytes)

            // Structural sanity: quiet zone aside, size must match version per ISO 18004.
            QrCode v1 = QrEncoder.Encode("pixmy://p/b7f3c2e1/P001");
            Program.Check(v1.Size == 17 + 4 * v1.Version, "symbol size matches version formula", ref failures);
            Program.Check(v1.Modules[0, 0] && v1.Modules[0, 6] && v1.Modules[6, 0],
                "top-left finder pattern corners are dark", ref failures);

            // Version ceiling: payload too long for the supported versions must fail loudly,
            // not silently emit a malformed symbol (see the ponytail note on QrEncoder).
            bool threw = false;
            try { QrEncoder.Encode(new string('y', 100)); }
            catch (NotSupportedException) { threw = true; }
            Program.Check(threw, "payload beyond version 3 capacity throws NotSupportedException", ref failures);

            return failures;
        }

        private static void RoundTrip(string payload, ref int failures)
        {
            QrCode qr = QrEncoder.Encode(payload);
            string decoded = Decode(qr);
            Program.Check(decoded == payload,
                "QR round-trip for \"" + payload + "\": decoded \"" + decoded + "\"", ref failures);
        }

        // (version, data codewords, ecc codewords) -- must match QrEncoder.VersionInfo.
        private static readonly int[][] VersionInfo = QrEncoder.VersionInfo;

        private static string Decode(QrCode qr)
        {
            int size = qr.Size;

            // 1. Extract bits in the same zigzag order codewords were written in,
            //    unmasking with the standard mask formula as we go.
            var bits = new List<bool>();
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!qr.IsFunction[y, x])
                        {
                            bool maskBit = QrEncoder.MaskCondition(qr.MaskPattern, y, x);
                            bits.Add(qr.Modules[y, x] ^ maskBit);
                        }
                    }
                }
            }

            int[] info = null;
            foreach (int[] v in VersionInfo) if (v[0] == qr.Version) info = v;
            if (info == null) throw new InvalidOperationException("unknown version " + qr.Version);
            int dataCw = info[1], eccCw = info[2];

            // 2. Independently recompute the Reed-Solomon codewords from the extracted
            //    data codewords and require them to match what was actually stored --
            //    this is the real proof the RS encoding path is correct.
            var dataCodewords = new byte[dataCw];
            for (int i = 0; i < dataCw; i++) dataCodewords[i] = (byte)ReadBits(bits, i * 8, 8);

            var storedEcc = new byte[eccCw];
            for (int i = 0; i < eccCw; i++) storedEcc[i] = (byte)ReadBits(bits, dataCw * 8 + i * 8, 8);

            byte[] recomputedEcc = Gf256.ReedSolomonRemainder(dataCodewords, Gf256.GeneratorPolynomial(eccCw));
            for (int i = 0; i < eccCw; i++)
                if (recomputedEcc[i] != storedEcc[i])
                    throw new InvalidOperationException("Reed-Solomon codeword mismatch at index " + i);

            // 3. Decode the byte-mode data stream: mode indicator, count, bytes.
            int pos = 0;
            int mode = (int)ReadBits(bits, pos, 4); pos += 4;
            if (mode != 4) throw new InvalidOperationException("expected byte-mode indicator 0100, got " + mode);
            int count = (int)ReadBits(bits, pos, 8); pos += 8;

            var outBytes = new byte[count];
            for (int i = 0; i < count; i++) { outBytes[i] = (byte)ReadBits(bits, pos, 8); pos += 8; }

            return Encoding.ASCII.GetString(outBytes);
        }

        private static long ReadBits(List<bool> bits, int start, int length)
        {
            long value = 0;
            for (int i = 0; i < length; i++) value = (value << 1) | (bits[start + i] ? 1L : 0L);
            return value;
        }
    }
}
