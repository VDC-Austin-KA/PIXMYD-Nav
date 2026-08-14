using System;
using System.Collections.Generic;
using System.Text;
using PIXMYD_Nav.Core.Markers;

namespace PIXMYD_Nav
{
    /// <summary>
    /// Proves QrEncoder correct against captured reference output from segno
    /// (the Python QR reference implementation), not against a decoder that
    /// shares the encoder's own logic. A prior version of this test round-
    /// tripped through a hand-written decoder built from the same placement/
    /// masking assumptions as the encoder, which meant a shared bug in those
    /// assumptions passed silently -- confirmed by cross-checking the old
    /// encoder output against segno module-for-module, which found ~40% of
    /// the data region wrong despite the round-trip test passing.
    ///
    /// The fixtures below are segno's exact matrix output for these payloads,
    /// generated offline via:
    ///   segno.make(payload, error='M', version=V, mask=M, boost_error=False, mode='byte')
    /// where V and M are whatever QrEncoder itself chose for that payload (so
    /// this only proves module-for-module identity, not that the encoder's
    /// own version/mask selection is "right" independent of segno -- but the
    /// encoder's version/mask choice is deterministic and covered implicitly:
    /// if it chose differently, the size assertion below would already fail).
    /// No python/segno dependency at runtime -- these are captured strings.
    /// </summary>
    internal static class QrEncoderTests
    {
        public static int Run()
        {
            int failures = 0;

            CheckFixture("pixmy://p/b7f3c2e1/P001", 2, 2, 25, RealPayloadV2Mask2, ref failures);
            CheckFixture("A", 1, 0, 21, OneByteV1Mask0, ref failures);
            CheckFixture(new string('x', 40), 3, 0, 29, NearCeilingV3Mask0, ref failures);

            // Round-trip is kept as a secondary smoke check, but the fixture
            // comparisons above are the real proof: a decoder built on the
            // same assumptions as the encoder can't catch a shared bug in
            // those assumptions (this is exactly how the old version of this
            // test missed a real defect -- see the class doc comment).
            RoundTrip("pixmy://p/b7f3c2e1/P001", ref failures);
            RoundTrip("pixmy://p/00000000/P999", ref failures);
            RoundTrip("A", ref failures);
            RoundTrip(new string('x', 40), ref failures);

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

        private static void CheckFixture(string payload, int expectedVersion, int expectedMask, int expectedSize,
            string[] expected, ref int failures)
        {
            QrCode qr = QrEncoder.Encode(payload);
            Program.Check(qr.Version == expectedVersion && qr.MaskPattern == expectedMask && qr.Size == expectedSize,
                "QR params for \"" + payload + "\": got v" + qr.Version + " mask" + qr.MaskPattern + " size" + qr.Size +
                ", expected v" + expectedVersion + " mask" + expectedMask + " size" + expectedSize, ref failures);

            int diffs = 0;
            for (int r = 0; r < qr.Size; r++)
                for (int c = 0; c < qr.Size; c++)
                    if (qr.Modules[r, c] != (expected[r][c] == '1')) diffs++;

            Program.Check(diffs == 0,
                "QR matrix for \"" + payload + "\" matches segno reference exactly (" + diffs + " module diffs)",
                ref failures);
        }

        // segno.make("pixmy://p/b7f3c2e1/P001", error='M', version=2, mask=2, boost_error=False, mode='byte').matrix
        private static readonly string[] RealPayloadV2Mask2 =
        {
            "1111111001111010001111111",
            "1000001001001001101000001",
            "1011101011000011101011101",
            "1011101010110101001011101",
            "1011101010101100101011101",
            "1000001010100111001000001",
            "1111111010101010101111111",
            "0000000011011010000000000",
            "1011111001100011101111100",
            "0010110111111010000001110",
            "0111111101010001110001011",
            "1010010100001000010000011",
            "1010101010010101011111100",
            "1001110011101010100100100",
            "1001011011000001101110011",
            "1011000010001000000110001",
            "1001001000101101111110101",
            "0000000010100010100011110",
            "1111111000111000101010011",
            "1000001011110001100011010",
            "1011101010111101111110111",
            "1011101011001011101011111",
            "1011101010101000010001101",
            "1000001001001011111111001",
            "1111111010101000010111111",
        };

        // segno.make("A", error='M', version=1, mask=0, boost_error=False, mode='byte').matrix
        private static readonly string[] OneByteV1Mask0 =
        {
            "111111100001101111111",
            "100000101101101000001",
            "101110100110101011101",
            "101110100100101011101",
            "101110101011101011101",
            "100000100101001000001",
            "111111101010101111111",
            "000000000100000000000",
            "101010100100100010010",
            "101110010111010101010",
            "001110101101011100101",
            "100110001101110111000",
            "101000100011011100101",
            "000000001100001000110",
            "111111100110100010011",
            "100000100100001000100",
            "101110101110101010101",
            "101110100101010101010",
            "101110101011011101101",
            "100000100101110111010",
            "111111101101011101111",
        };

        // segno.make("x"*40, error='M', version=3, mask=0, boost_error=False, mode='byte').matrix
        private static readonly string[] NearCeilingV3Mask0 =
        {
            "11111110010011010101001111111",
            "10000010110111010101001000001",
            "10111010001110101010101011101",
            "10111010001000101010101011101",
            "10111010110011010101001011101",
            "10000010011011010101001000001",
            "11111110101010101010101111111",
            "00000000011011010101000000000",
            "10101010010001010101000010010",
            "01011000111110101010101001101",
            "11101010101110101010100010111",
            "10011101001101010101010110010",
            "11100110110101010101011101000",
            "11011001011000101010101001101",
            "10011011101000101010100010111",
            "11001101100011010101010110010",
            "10011011010100110101011101000",
            "00111001010011001010101001101",
            "10011010100000101010100010111",
            "01101001010101010101010110010",
            "10111111101100110101111111000",
            "00000000100110001010100011101",
            "11111110010001001011101010111",
            "10000010001000010100100010010",
            "10111010110100010100111111000",
            "10111010010010001011000011111",
            "10111010101000101010010110101",
            "10000010010011110100111100010",
            "11111110110011010101101001011",
        };

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
            // Note: this does not read the terminator/pad bits (including the
            // segno-matching "always pad a full byte if already byte-aligned"
            // quirk in QrEncoder.BuildDataCodewords) -- it only needs the mode,
            // count and payload bytes to reconstruct the original string, so
            // that quirk is invisible here and is covered by the fixtures above.
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
