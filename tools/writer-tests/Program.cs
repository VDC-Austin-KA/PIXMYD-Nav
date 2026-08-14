using System;

namespace PIXMY4D_Nav
{
    internal static class Program
    {
        private static int Main()
        {
            int failures = 0;
            failures += PointSetTests.Run();
            failures += LevelNameTests.Run();
            failures += QrEncoderTests.Run();
            failures += MarkerPageTests.Run();

            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        internal static void Check(bool condition, string message, ref int failures)
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + message);
        }
    }
}
