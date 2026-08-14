using System;
using System.Collections.Generic;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Json;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The return leg: reading a capture.json off a phone and working out where
    /// its mesh belongs in the model.
    ///
    /// The fixture is the exact output of PIXMYD's CaptureExport.render -- same
    /// field order, same number formatting, same provenance block. Testing
    /// against the producer's real bytes rather than against the contract prose
    /// is deliberate: the prose is the agreement, the bytes are what has to parse
    /// on a workstation.
    ///
    /// The solver vectors are the same ones PIXMYD's TypeScript and Swift suites
    /// use. Three copies of one algorithm can only stay honest if they are
    /// checked against the same numbers.
    /// </summary>
    internal static class CaptureTests
    {
        public static int Run()
        {
            int failures = 0;

            JsonReaderBasics(ref failures);
            ReadsARealCapture(ref failures);
            VersionGate(ref failures);
            DegradedModeIsNotAnError(ref failures);
            SolvesLocally(ref failures);
            SolverRefusesWhatItCannotDetermine(ref failures);
            PlacementAddsTheOriginOffset(ref failures);
            AccuracyBandsMatchTheContract(ref failures);

            return failures;
        }

        // MARK: - Fixtures

        private const string RealCapture = @"{
  ""contractVersion"": ""1.0"",
  ""captureId"": ""44e0b8a2-0000-0000-0000-000000000000"",
  ""capturedUtc"": ""2026-02-02T02:40:00.000Z"",
  ""pointSetId"": ""b7f3c2e1-1111-2222-3333-444444444444"",
  ""device"": {
    ""model"": ""iPhone16,1"",
    ""hasLidar"": true
  },
  ""correspondences"": [
    { ""pointId"": ""P001"", ""observed"": [ 17.4, 6.15, 1.0 ] },
    { ""pointId"": ""P002"", ""observed"": [ 25.4, 6.15, 1.0 ] },
    { ""pointId"": ""P003"", ""observed"": [ 17.4, 14.15, 1.0 ] }
  ],
  ""solution"": {
    ""matrix"": [ 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -5.0, 2.0, -1.0, 1.0 ],
    ""scale"": 1.0,
    ""rmsError"": 0.0,
    ""maxError"": 0.0,
    ""accuracyGrade"": ""layout"",
    ""outlierPointIds"": []
  },
  ""geometry"": {
    ""file"": ""capture.glb"",
    ""bytes"": 12882110
  },
  ""provenance"": {
    ""navex:sourceDocument"": ""TowerA.nwd"",
    ""navex:sourceUnits"": ""Feet"",
    ""navex:targetUnits"": ""Meters"",
    ""navex:upAxis"": ""Z"",
    ""navex:originMode"": ""ModelMin"",
    ""navex:appliedOffset"": [ -1204.5, 883.2, 0.0 ],
    ""navex:offsetNote"": ""Add appliedOffset to exported coordinates to return to source world coordinates."",
    ""navex:exportedUtc"": ""2026-08-13T14:02:11.000Z""
  }
}";

        /// <summary>The point set the fixture was captured against.</summary>
        private static Dictionary<string, double[]> FixturePoints()
        {
            var points = new Dictionary<string, double[]>();
            points["P001"] = new double[] { 12.4, 8.15, 0.0 };
            points["P002"] = new double[] { 20.4, 8.15, 0.0 };
            points["P003"] = new double[] { 12.4, 16.15, 0.0 };
            return points;
        }

        // MARK: - JSON reader

        private static void JsonReaderBasics(ref int failures)
        {
            JsonValue root = JsonReader.Parse(
                "{\"a\":1,\"b\":\"two\",\"c\":[1,2,3],\"d\":true,\"e\":null,\"f\":{\"g\":-1.5e2}}");

            Program.Check(root["a"].AsNumber(0) == 1, "number", ref failures);
            Program.Check(root["b"].AsString("") == "two", "string", ref failures);
            Program.Check(root["c"].Count == 3, "array length", ref failures);
            Program.Check(root["d"].AsBool(false), "true", ref failures);
            Program.Check(root["e"].IsNull, "null", ref failures);
            Program.Check(root["f"]["g"].AsNumber(0) == -150, "nested exponent", ref failures);

            // A missing key is the normal case for an optional contract field
            // and must not throw.
            Program.Check(root["nope"] == null, "a missing member reads as null", ref failures);

            Program.Check(JsonReader.Parse("\"a\\nb\\u0041\"").AsString("") == "a\nbA",
                "escapes", ref failures);

            // Strict: a file this cannot read should say so rather than be
            // repaired into a plausible placement.
            Rejects("{\"a\":1,}", "trailing comma", ref failures);
            Rejects("{\"a\":}", "missing value", ref failures);
            Rejects("{a:1}", "unquoted name", ref failures);
            Rejects("[1,2", "unclosed array", ref failures);
            Rejects("{} junk", "trailing text", ref failures);
            Rejects("", "empty", ref failures);
        }

        private static void Rejects(string json, string why, ref int failures)
        {
            bool threw = false;
            try { JsonReader.Parse(json); }
            catch (JsonParseException) { threw = true; }
            Program.Check(threw, "rejects " + why, ref failures);
        }

        // MARK: - capture.json

        private static void ReadsARealCapture(ref int failures)
        {
            CaptureFile capture = CaptureReader.Read(RealCapture);

            Program.Check(capture.CaptureId == "44e0b8a2-0000-0000-0000-000000000000", "captureId", ref failures);
            Program.Check(capture.PointSetId == "b7f3c2e1-1111-2222-3333-444444444444", "pointSetId", ref failures);
            Program.Check(capture.DeviceModel == "iPhone16,1", "device model", ref failures);
            Program.Check(capture.DeviceHasLidar, "device hasLidar", ref failures);
            Program.Check(capture.Correspondences.Count == 3, "three correspondences", ref failures);
            Program.Check(capture.Correspondences[0].PointId == "P001", "first correspondence id", ref failures);
            Program.Check(capture.Correspondences[0].Observed[0] == 17.4, "observed x", ref failures);

            Program.Check(capture.HasSolution, "carries a solution", ref failures);
            Program.Check(capture.Solution.Matrix.Length == 16, "matrix is 4x4", ref failures);
            Program.Check(capture.Solution.Scale == 1.0, "scale is fixed at 1", ref failures);
            Program.Check(capture.Solution.AccuracyGrade == "layout", "accuracy grade", ref failures);
            Program.Check(capture.Solution.OutlierPointIds.Length == 0, "no outliers", ref failures);
            Program.Check(!capture.Solution.SolvedLocally, "the phone solved it", ref failures);

            Program.Check(capture.GeometryFile == "capture.glb", "geometry file", ref failures);
            Program.Check(capture.GeometryBytes == 12882110, "geometry size", ref failures);

            // The offset that gets the mesh back to model world coordinates.
            Program.Check(capture.AppliedOffset[0] == -1204.5, "appliedOffset x", ref failures);
            Program.Check(capture.AppliedOffset[1] == 883.2, "appliedOffset y", ref failures);
            Program.Check(capture.TargetUnits == "Meters", "target units", ref failures);
        }

        private static void VersionGate(ref int failures)
        {
            string bumped = RealCapture.Replace("\"contractVersion\": \"1.0\"", "\"contractVersion\": \"1.7\"");
            CaptureFile minor = CaptureReader.Read(bumped);
            Program.Check(minor.Correspondences.Count == 3,
                "a minor version bump is still readable", ref failures);

            string major = RealCapture.Replace("\"contractVersion\": \"1.0\"", "\"contractVersion\": \"2.0\"");
            string message = null;
            try { CaptureReader.Read(major); }
            catch (CaptureReadException e) { message = e.Message; }
            Program.Check(message != null && message.Contains("2.0"),
                "a major version bump is refused and names the version", ref failures);

            string none = RealCapture.Replace("\"contractVersion\": \"1.0\",", "");
            message = null;
            try { CaptureReader.Read(none); }
            catch (CaptureReadException e) { message = e.Message; }
            Program.Check(message != null, "a file with no contractVersion is refused", ref failures);
        }

        /// <summary>
        /// "solution absent but correspondences present -> offer to solve
        /// locally. This is the useful degraded mode, not an error."
        /// </summary>
        private static void DegradedModeIsNotAnError(ref int failures)
        {
            int start = RealCapture.IndexOf("  \"solution\"", StringComparison.Ordinal);
            int end = RealCapture.IndexOf("  \"geometry\"", StringComparison.Ordinal);
            string without = RealCapture.Substring(0, start) + RealCapture.Substring(end);

            CaptureFile capture = CaptureReader.Read(without);
            Program.Check(!capture.HasSolution, "no solution present", ref failures);
            Program.Check(capture.Correspondences.Count == 3,
                "the raw observations still came home", ref failures);

            CaptureSolution solved = CaptureReader.SolveLocally(capture, FixturePoints());
            Program.Check(solved.SolvedLocally, "flagged as solved here, not on the phone", ref failures);
            Program.Check(solved.RmsError < 1e-9, "clean control solves exactly", ref failures);
        }

        /// <summary>
        /// The whole point of shipping raw correspondences: the consumer can
        /// re-solve rather than trust a number it cannot check. So the local
        /// solve has to reproduce the phone's answer.
        /// </summary>
        private static void SolvesLocally(ref int failures)
        {
            CaptureFile capture = CaptureReader.Read(RealCapture);
            CaptureSolution local = CaptureReader.SolveLocally(capture, FixturePoints());

            Program.Check(local.RmsError < 1e-9,
                "local solve is exact on clean control, got " + local.RmsError, ref failures);
            Program.Check(local.Scale == 1.0, "scale stays fixed at 1", ref failures);
            Program.Check(local.AccuracyGrade == "layout", "grades as layout", ref failures);

            // The capture frame is the point frame translated by (5, -2, 1), so
            // the transform back is (-5, 2, -1) with no rotation.
            Program.Check(Math.Abs(local.Matrix[12] - (-5.0)) < 1e-9,
                "translation x, got " + local.Matrix[12], ref failures);
            Program.Check(Math.Abs(local.Matrix[13] - 2.0) < 1e-9,
                "translation y, got " + local.Matrix[13], ref failures);
            Program.Check(Math.Abs(local.Matrix[14] - (-1.0)) < 1e-9,
                "translation z, got " + local.Matrix[14], ref failures);

            // And it agrees with what the phone wrote, which is the check that
            // matters -- three implementations of one algorithm.
            for (int i = 0; i < 16; i++)
                Program.Check(Math.Abs(local.Matrix[i] - capture.Solution.Matrix[i]) < 1e-9,
                    "local matrix element " + i + " matches the phone's", ref failures);
        }

        private static void SolverRefusesWhatItCannotDetermine(ref int failures)
        {
            var twoPoints = new List<ControlPair>();
            twoPoints.Add(new ControlPair("A", new double[] { 0, 0, 0 }, new double[] { 1, 1, 1 }));
            twoPoints.Add(new ControlPair("B", new double[] { 1, 0, 0 }, new double[] { 2, 1, 1 }));
            Program.Check(Throws(twoPoints), "two points are refused", ref failures);

            // Collinear: the rotation about the line is undetermined, and a
            // solve would return an arbitrary one that looks like an answer.
            var collinear = new List<ControlPair>();
            collinear.Add(new ControlPair("A", new double[] { 0, 0, 0 }, new double[] { 0, 0, 0 }));
            collinear.Add(new ControlPair("B", new double[] { 1, 0, 0 }, new double[] { 1, 0, 0 }));
            collinear.Add(new ControlPair("C", new double[] { 2, 0, 0 }, new double[] { 2, 0, 0 }));
            Program.Check(Throws(collinear), "collinear control is refused", ref failures);

            var coincident = new List<ControlPair>();
            for (int i = 0; i < 3; i++)
                coincident.Add(new ControlPair("P" + i, new double[] { 1, 1, 1 }, new double[] { 2, 2, 2 }));
            Program.Check(Throws(coincident), "coincident control is refused", ref failures);
        }

        private static bool Throws(List<ControlPair> pairs)
        {
            try { RigidSolve.Solve(pairs); return false; }
            catch (RigidSolveException) { return true; }
        }

        // MARK: - Placement

        /// <summary>
        /// "PIXMYD-Nav applies solution.matrix, then adds appliedOffset from the
        /// point set's provenance, to land the mesh in model world coordinates."
        /// </summary>
        private static void PlacementAddsTheOriginOffset(ref int failures)
        {
            CaptureFile capture = CaptureReader.Read(RealCapture);

            // P001 was observed at (17.4, 6.15, 1.0) in the capture frame. The
            // matrix should put it back at the model coordinate (12.4, 8.15, 0),
            // and the offset then takes it to source world space.
            double[] observed = new double[] { 17.4, 6.15, 1.0 };
            double[] inSet = CapturePlacement.Transform(capture.Solution.Matrix, observed);
            Program.Check(Math.Abs(inSet[0] - 12.4) < 1e-9, "mapped x, got " + inSet[0], ref failures);
            Program.Check(Math.Abs(inSet[1] - 8.15) < 1e-9, "mapped y, got " + inSet[1], ref failures);
            Program.Check(Math.Abs(inSet[2] - 0.0) < 1e-9, "mapped z, got " + inSet[2], ref failures);

            double[] world = CapturePlacement.ToModelWorld(
                capture.Solution.Matrix, capture.AppliedOffset, observed);
            Program.Check(Math.Abs(world[0] - (12.4 - 1204.5)) < 1e-9, "world x", ref failures);
            Program.Check(Math.Abs(world[1] - (8.15 + 883.2)) < 1e-9, "world y", ref failures);

            // The folded matrix has to agree with doing it in two steps, or the
            // mesh and the points would land in different places.
            double[] folded = CapturePlacement.ModelWorldMatrix(capture.Solution.Matrix, capture.AppliedOffset);
            double[] viaMatrix = CapturePlacement.Transform(folded, observed);
            for (int i = 0; i < 3; i++)
                Program.Check(Math.Abs(viaMatrix[i] - world[i]) < 1e-9,
                    "folded matrix agrees on axis " + i, ref failures);
        }

        // MARK: - Grading

        private static void AccuracyBandsMatchTheContract(ref int failures)
        {
            Program.Check(AccuracyBands.Classify(0.002).Band == "layout", "3 mm is layout", ref failures);
            Program.Check(AccuracyBands.Classify(0.005).Band == "penetrations", "6 mm is penetrations", ref failures);
            Program.Check(AccuracyBands.Classify(0.009).Band == "dimensional-control",
                "10 mm is dimensional control", ref failures);
            Program.Check(AccuracyBands.Classify(0.04).Band == "coordination", "50 mm is coordination", ref failures);
            Program.Check(AccuracyBands.Classify(0.2).Band == "context", "250 mm is context", ref failures);
            Program.Check(AccuracyBands.Classify(1.0).Band == "unusable", "a metre is unusable", ref failures);

            // The gate the contract requires: confirmation below survey
            // tolerance. Silently placing a capture 300 mm out is worse than
            // refusing to place it.
            Program.Check(AccuracyBands.Classify(0.009).WithinSurveyTolerance,
                "dimensional control is within tolerance", ref failures);
            Program.Check(!AccuracyBands.Classify(0.04).WithinSurveyTolerance,
                "coordination grade needs confirmation", ref failures);
            Program.Check(!AccuracyBands.Classify(0.3).WithinSurveyTolerance,
                "300 mm needs confirmation", ref failures);
        }
    }
}
