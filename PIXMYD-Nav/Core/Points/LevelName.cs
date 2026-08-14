namespace PIXMYD_Nav.Core.Points
{
    /// <summary>
    /// Pure level-name normalisation, copied verbatim from
    /// AutoNAV2\AutoNAV\ClashGrouper.cs (NormaliseLevel, around line 865). Zero
    /// Navisworks calls -- kept as its own file so it can be offline-tested.
    /// </summary>
    public static class LevelName
    {
        // Converts "Level 3", "L3", "L03", "Floor 03" to "L03"; "Basement 1" / "B1" to "B01";
        // returns the original string if no clean pattern matches.
        public static string Normalise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            string s = raw.Trim();

            // Pull the first numeric run; preserve the lead character if it's a
            // recognised level prefix (L, B, M, R, T, P).
            int firstDigit = -1;
            for (int i = 0; i < s.Length; i++) { if (char.IsDigit(s[i])) { firstDigit = i; break; } }
            if (firstDigit < 0) return s; // no number -- return as-is

            int end = firstDigit;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            string numPart = s.Substring(firstDigit, end - firstDigit);
            int num;
            if (!int.TryParse(numPart, out num)) return s;
            string numFmt = num.ToString("D2");

            string lower = s.ToLowerInvariant();
            if (lower.Contains("base") || lower.StartsWith("b")) return "B" + numFmt;
            if (lower.Contains("roof")) return "R" + numFmt;
            if (lower.Contains("mezz")) return "M" + numFmt;
            if (lower.Contains("park")) return "P" + numFmt;
            if (lower.Contains("term")) return "T" + numFmt;
            // Default to "L" prefix for levels / floors / generic.
            return "L" + numFmt;
        }
    }
}
