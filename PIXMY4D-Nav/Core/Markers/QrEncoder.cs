using System;
using System.Collections.Generic;
using System.Text;

namespace PIXMY4D_Nav.Core.Markers
{
    /// <summary>A finished QR symbol: the module grid plus the parameters used to build it.</summary>
    public sealed class QrCode
    {
        public int Version;
        public int Size;
        public int MaskPattern;
        public bool[,] Modules; // [row, col], true = dark

        // Function-pattern mask (finder/timing/alignment/format/dark module).
        // Exposed so tests can round-trip decode without re-deriving QR geometry.
        public bool[,] IsFunction;
    }

    /// <summary>
    /// Minimal QR encoder: byte mode only, fixed error-correction level M, smallest
    /// version that fits. The payload this plugin encodes is short and fixed-format
    /// (pixmy://p/xxxxxxxx/Pnnn, ~23 bytes), so the general QR case never arises.
    /// No NuGet dependency -- follows ISO/IEC 18004 directly (data encoding, Reed-
    /// Solomon error correction, module placement, masking).
    ///
    /// ponytail: only versions 1-3 are implemented (max 42 data bytes at level M).
    /// Versions 4+ split data across multiple Reed-Solomon blocks with interleaving,
    /// which this does not do. Throws NotSupportedException rather than emit a
    /// malformed symbol if a payload ever needs a bigger version -- extend
    /// EncodeCodewords' block handling if that happens. The documented fallback if
    /// this ever proves unreliable against real scanners is QRCoder (MIT, no
    /// transitive deps) -- see docs/work-orders/pixmy4d-nav.md task P2.
    /// </summary>
    public static class QrEncoder
    {
        // (version, data codewords, ecc codewords) at error-correction level M.
        // Each of these versions uses exactly one Reed-Solomon block, which is
        // what keeps this encoder simple.
        internal static readonly int[][] VersionInfo =
        {
            new[] { 1, 16, 10 },
            new[] { 2, 28, 16 },
            new[] { 3, 44, 26 },
        };

        // Alignment pattern center coordinates per version (empty for version 1).
        private static readonly Dictionary<int, int[]> AlignmentCenters = new Dictionary<int, int[]>
        {
            { 1, new int[0] },
            { 2, new[] { 6, 18 } },
            { 3, new[] { 6, 22 } },
        };

        private const int EcLevelBitsM = 0; // 2-bit ECC-level indicator for level M per ISO 18004 Table 25

        public static QrCode Encode(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            byte[] data = Encoding.ASCII.GetBytes(text);

            int[] chosen = null;
            foreach (int[] v in VersionInfo)
            {
                int candidateDataCw = v[1];
                int capacity = (candidateDataCw * 8 - 12) / 8; // minus 4-bit mode + 8-bit count indicator
                if (data.Length <= capacity) { chosen = v; break; }
            }
            if (chosen == null)
                throw new NotSupportedException(
                    "QrEncoder only supports versions 1-3 (level M, max 42 bytes); payload is " +
                    data.Length + " bytes. See the ponytail note on QrEncoder for the extension path.");

            int version = chosen[0], dataCw = chosen[1], eccCw = chosen[2];

            byte[] dataCodewords = BuildDataCodewords(data, dataCw);
            byte[] eccCodewords = Gf256.ReedSolomonRemainder(dataCodewords, Gf256.GeneratorPolynomial(eccCw));

            byte[] allCodewords = new byte[dataCw + eccCw];
            Array.Copy(dataCodewords, allCodewords, dataCw);
            Array.Copy(eccCodewords, 0, allCodewords, dataCw, eccCw);

            return BuildMatrix(version, allCodewords);
        }

        private static byte[] BuildDataCodewords(byte[] data, int dataCw)
        {
            var bits = new List<bool>();
            AppendBits(bits, 4, 4);              // byte-mode indicator
            AppendBits(bits, data.Length, 8);     // character count indicator (versions 1-9)
            foreach (byte b in data) AppendBits(bits, b, 8);

            int targetBits = dataCw * 8;
            int terminator = Math.Min(4, targetBits - bits.Count);
            if (terminator > 0) AppendBits(bits, 0, terminator);

            // Byte-boundary padding. Reference behaviour (segno, ISO/IEC 18004
            // 7.4.10): always pad by (8 - len%8) bits, even when already
            // aligned -- that adds a full zero byte in the aligned case. Not
            // "only pad when misaligned"; matched exactly for bit-for-bit
            // compatibility with the reference encoder.
            AppendBits(bits, 0, 8 - (bits.Count % 8));

            byte[] pad = { 0xEC, 0x11 };
            int padIndex = 0;
            while (bits.Count < targetBits) { AppendBits(bits, pad[padIndex % 2], 8); padIndex++; }

            var codewords = new byte[dataCw];
            for (int i = 0; i < dataCw; i++)
            {
                int b = 0;
                for (int bit = 0; bit < 8; bit++) b = (b << 1) | (bits[i * 8 + bit] ? 1 : 0);
                codewords[i] = (byte)b;
            }
            return codewords;
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        // ---- Module placement ----

        private static QrCode BuildMatrix(int version, byte[] codewords)
        {
            int size = 17 + 4 * version;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];

            DrawFinderPattern(modules, isFunction, size, 0, 0);
            DrawFinderPattern(modules, isFunction, size, 0, size - 7);
            DrawFinderPattern(modules, isFunction, size, size - 7, 0);
            DrawTimingPatterns(modules, isFunction, size);
            DrawAlignmentPatterns(modules, isFunction, size, AlignmentCenters[version]);
            ReserveFormatArea(isFunction, size); // mark format-info cells as function (stays light) so codewords skip them

            var dataBits = new List<bool>();
            foreach (byte b in codewords) AppendBits(dataBits, b, 8);
            DrawCodewordBits(modules, isFunction, size, dataBits);

            // Mask trial: do NOT draw format info in advance of evaluation (ISO/IEC
            // 18004 7.8 Data masking is scored before format info exists; the format
            // bits themselves depend on the chosen mask, so scoring with a guessed
            // format value would bias the choice). Format-info cells stay light
            // (reserved above, never touched by ApplyMask since isFunction is set)
            // during every trial.
            int bestMask = -1, bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunction, size, mask);
                int penalty = Penalty(modules, size);
                if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
                ApplyMask(modules, isFunction, size, mask); // undo (XOR is self-inverse)
            }

            ApplyMask(modules, isFunction, size, bestMask);
            DrawFormatBits(modules, isFunction, size, EcLevelBitsM, bestMask);

            return new QrCode { Version = version, Size = size, MaskPattern = bestMask, Modules = modules, IsFunction = isFunction };
        }

        private static readonly bool[,] FinderCore =
        {
            { true, true, true, true, true, true, true },
            { true, false, false, false, false, false, true },
            { true, false, true, true, true, false, true },
            { true, false, true, true, true, false, true },
            { true, false, true, true, true, false, true },
            { true, false, false, false, false, false, true },
            { true, true, true, true, true, true, true },
        };

        private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int size, int row, int col)
        {
            for (int dy = -1; dy <= 7; dy++)
                for (int dx = -1; dx <= 7; dx++)
                {
                    int r = row + dy, c = col + dx;
                    if (r < 0 || r >= size || c < 0 || c >= size) continue;
                    bool dark = dy >= 0 && dy <= 6 && dx >= 0 && dx <= 6 && FinderCore[dy, dx];
                    modules[r, c] = dark;
                    isFunction[r, c] = true;
                }
        }

        private static void DrawTimingPatterns(bool[,] modules, bool[,] isFunction, int size)
        {
            for (int i = 8; i <= size - 9; i++)
            {
                bool dark = i % 2 == 0;
                modules[6, i] = dark; isFunction[6, i] = true;
                modules[i, 6] = dark; isFunction[i, 6] = true;
            }
        }

        private static readonly bool[,] AlignmentCore =
        {
            { true, true, true, true, true },
            { true, false, false, false, true },
            { true, false, true, false, true },
            { true, false, false, false, true },
            { true, true, true, true, true },
        };

        private static void DrawAlignmentPatterns(bool[,] modules, bool[,] isFunction, int size, int[] centers)
        {
            foreach (int r in centers)
                foreach (int c in centers)
                {
                    // Skip positions that would overlap a finder pattern's 8x8 zone.
                    bool overlapsFinder = (r <= 8 && c <= 8) || (r <= 8 && c >= size - 9) || (r >= size - 9 && c <= 8);
                    if (overlapsFinder) continue;

                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            modules[r + dy, c + dx] = AlignmentCore[dy + 2, dx + 2];
                            isFunction[r + dy, c + dx] = true;
                        }
                }
        }

        // ISO 18004 Annex C: BCH(15,5) format-info encoding, generator 0x537, XOR mask 0x5412.
        private static int ComputeFormatBits(int ecLevelBits, int maskPattern)
        {
            int data = (ecLevelBits << 3) | maskPattern;
            int rem = data << 10;
            for (int i = 4; i >= 0; i--)
                if (((rem >> (i + 10)) & 1) != 0) rem ^= 0x537 << i;
            return ((data << 10) | rem) ^ 0x5412;
        }

        // Format info is drawn as two interleaved copies -- one read LSB-first
        // (vbit), one MSB-first (hbit) -- around the two corners nearest the
        // format strip. This is not a simple "reverse the bits" rule; ISO/IEC
        // 18004 7.9 (Fig. 25) genuinely alternates convention by axis, and the
        // row/col index skips the timing-pattern line (index 6) via voffset/
        // hoffset. Transcribed directly from the reference layout rather than
        // derived, since guessing at "the" bit order here is exactly what
        // produced the earlier bug.
        private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int size, int ecLevelBits, int maskPattern)
        {
            int formatInfo = ComputeFormatBits(ecLevelBits, maskPattern);
            int voffset = 0, hoffset = 0;
            for (int i = 0; i < 8; i++)
            {
                bool vbit = ((formatInfo >> i) & 1) != 0;
                bool hbit = ((formatInfo >> (14 - i)) & 1) != 0;
                if (i == 6) { voffset = 1; hoffset = 1; }
                SetFn(modules, isFunction, i + voffset, 8, vbit);       // vertical, upper-left corner
                SetFn(modules, isFunction, 8, i + hoffset, hbit);       // horizontal, upper-left corner
                SetFn(modules, isFunction, 8, size - 1 - i, vbit);      // horizontal, upper-right corner
                SetFn(modules, isFunction, size - 1 - i, 8, hbit);      // vertical, bottom-left corner
            }
            SetFn(modules, isFunction, size - 8, 8, true); // the fixed dark module
        }

        // Marks the format-info cells as function modules (excluded from data
        // placement and masking) without writing their final values -- the real
        // format bits depend on the mask chosen by the trial below, so ISO/IEC
        // 18004 7.8 scores masks against the format area left light, then the
        // caller draws the real bits with DrawFormatBits once a mask is picked.
        // Position set mirrors DrawFormatBits exactly.
        private static void ReserveFormatArea(bool[,] isFunction, int size)
        {
            int voffset = 0, hoffset = 0;
            for (int i = 0; i < 8; i++)
            {
                if (i == 6) { voffset = 1; hoffset = 1; }
                isFunction[i + voffset, 8] = true;
                isFunction[8, i + hoffset] = true;
                isFunction[8, size - 1 - i] = true;
                isFunction[size - 1 - i, 8] = true;
            }
            isFunction[size - 8, 8] = true;
        }

        private static void SetFn(bool[,] modules, bool[,] isFunction, int r, int c, bool dark)
        {
            modules[r, c] = dark;
            isFunction[r, c] = true;
        }

        // Zigzag placement of data/ecc bits into every non-function module, two
        // columns at a time from the bottom-right, skipping the timing column.
        private static void DrawCodewordBits(bool[,] modules, bool[,] isFunction, int size, List<bool> bits)
        {
            int i = 0;
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
                        if (!isFunction[y, x] && i < bits.Count)
                        {
                            modules[y, x] = bits[i];
                            i++;
                        }
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int size, int mask)
        {
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!isFunction[r, c] && MaskCondition(mask, r, c))
                        modules[r, c] = !modules[r, c];
        }

        internal static bool MaskCondition(int mask, int r, int c)
        {
            switch (mask)
            {
                case 0: return (r + c) % 2 == 0;
                case 1: return r % 2 == 0;
                case 2: return c % 3 == 0;
                case 3: return (r + c) % 3 == 0;
                case 4: return (r / 2 + c / 3) % 2 == 0;
                case 5: return (r * c) % 2 + (r * c) % 3 == 0;
                case 6: return ((r * c) % 2 + (r * c) % 3) % 2 == 0;
                case 7: return ((r + c) % 2 + (r * c) % 3) % 2 == 0;
                default: throw new ArgumentOutOfRangeException("mask");
            }
        }

        // ISO 18004 Annex A: the four masking penalty rules.
        private static int Penalty(bool[,] m, int size)
        {
            int penalty = 0;

            for (int r = 0; r < size; r++) penalty += RunPenalty(c => m[r, c], size);
            for (int c = 0; c < size; c++) penalty += RunPenalty(r => m[r, c], size);

            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                    if (m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c] && m[r, c] == m[r + 1, c + 1])
                        penalty += 3;

            for (int r = 0; r < size; r++) penalty += FinderLikePenalty(c => m[r, c], size);
            for (int c = 0; c < size; c++) penalty += FinderLikePenalty(r => m[r, c], size);

            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;
            int percent = dark * 100 / (size * size);
            int prev = percent - percent % 5, next = prev + 5;
            penalty += Math.Min(Math.Abs(prev - 50) / 5, Math.Abs(next - 50) / 5) * 10;

            return penalty;
        }

        private static int RunPenalty(Func<int, bool> at, int size)
        {
            int penalty = 0, runLen = 1;
            bool prev = at(0);
            for (int i = 1; i < size; i++)
            {
                bool cur = at(i);
                if (cur == prev) { runLen++; }
                else { if (runLen >= 5) penalty += 3 + (runLen - 5); runLen = 1; prev = cur; }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
            return penalty;
        }

        // 1:1:3:1:1 dark:light:dark:dark:dark:light:dark core, scored once per
        // occurrence if it borders a light run of >=4 (on either side) or the
        // symbol edge -- ISO/IEC 18004 7.8.3.1 N3. Matches segno's search
        // algorithm exactly: an occurrence that qualifies skips past its whole
        // core+margin before searching for the next one; one that doesn't
        // qualify only advances 4 (so overlapping cores still get checked).
        private static readonly bool[] FinderLikeCore = { true, false, true, true, true, false, true };

        private static int FinderLikePenalty(Func<int, bool> at, int size)
        {
            int penalty = 0;
            int idx = FindCore(at, size, 0);
            while (idx != -1)
            {
                int after = idx + 7;
                bool lightBefore = !AnyDark(at, Math.Max(idx - 4, 0), Math.Min(idx, size));
                bool lightAfter = !AnyDark(at, Math.Max(after, 0), Math.Min(after + 4, size));
                int next;
                if (idx == 0 || idx == size - 7 || lightBefore || lightAfter)
                {
                    penalty += 40;
                    next = after;
                }
                else
                {
                    next = idx + 4;
                }
                idx = FindCore(at, size, next);
            }
            return penalty;
        }

        private static int FindCore(Func<int, bool> at, int size, int start)
        {
            for (int i = start; i <= size - 7; i++)
                if (Matches(at, i, FinderLikeCore)) return i;
            return -1;
        }

        private static bool AnyDark(Func<int, bool> at, int from, int to)
        {
            for (int i = from; i < to; i++)
                if (at(i)) return true;
            return false;
        }

        private static bool Matches(Func<int, bool> at, int start, bool[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
                if (at(start + i) != pattern[i]) return false;
            return true;
        }
    }

    /// <summary>GF(256) arithmetic and Reed-Solomon codeword generation for QR's
    /// error correction, per ISO/IEC 18004 Annex A (primitive polynomial 0x11D).</summary>
    internal static class Gf256
    {
        private static readonly int[] ExpTable = new int[512];
        private static readonly int[] LogTable = new int[256];

        static Gf256()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                ExpTable[i] = x;
                LogTable[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) ExpTable[i] = ExpTable[i - 255];
        }

        private static int Mul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return ExpTable[LogTable[a] + LogTable[b]];
        }

        /// <summary>Builds the Reed-Solomon generator polynomial of the given degree
        /// (highest-degree coefficient first, implicit leading 1).</summary>
        public static byte[] GeneratorPolynomial(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = (byte)Mul(result[j] & 0xFF, root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = Mul(root, 2);
            }
            return result;
        }

        public static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++)
                    result[i] ^= (byte)Mul(divisor[i] & 0xFF, factor);
            }
            return result;
        }
    }
}
