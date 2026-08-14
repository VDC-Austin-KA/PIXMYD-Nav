using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav
{
    internal static class LevelNameTests
    {
        public static int Run()
        {
            int failures = 0;

            Check("Level 3", "L03", ref failures);
            Check("L3", "L03", ref failures);
            Check("L03", "L03", ref failures);
            Check("Floor 03", "L03", ref failures);
            Check("Basement 1", "B01", ref failures);
            Check("B1", "B01", ref failures);
            Check("Roof 2", "R02", ref failures);
            Check("Mezzanine 1", "M01", ref failures);
            Check("Parking 4", "P04", ref failures);
            Check("Terminal 1", "T01", ref failures);
            Check("No Number Here", "No Number Here", ref failures); // no digit -- returned as-is
            Program.Check(LevelName.Normalise("") == "", "empty string returns as-is", ref failures);
            Program.Check(LevelName.Normalise(null) == null, "null returns as-is", ref failures);

            return failures;
        }

        private static void Check(string input, string expected, ref int failures)
        {
            string actual = LevelName.Normalise(input);
            Program.Check(actual == expected,
                "Normalise(\"" + input + "\") expected \"" + expected + "\" got \"" + actual + "\"", ref failures);
        }
    }
}
