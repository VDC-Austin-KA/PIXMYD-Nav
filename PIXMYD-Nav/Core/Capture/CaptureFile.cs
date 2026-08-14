using System;
using System.Collections.Generic;
using System.Globalization;
using PIXMYD_Nav.Core.Json;

namespace PIXMYD_Nav.Core.Capture
{
    /// <summary>
    /// Reading a capture.json back off a phone, per docs/contracts/capture.md.
    ///
    /// The return leg: a real-world scan comes back into Navisworks already
    /// positioned, because it was captured against points both sides know.
    ///
    /// This file only reads and reasons. It does not touch Navisworks and it does
    /// not place anything -- the caller shows the numbers, asks, and then places.
    /// That separation is the contract's one hard rule for this direction:
    /// "silently placing a capture that is 300 mm out is worse than refusing to
    /// place it", and a decision that lives inside a parser cannot be shown to
    /// anyone.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class CaptureCorrespondence
    {
        public string PointId;
        /// <summary>In the capture's own frame, metres, before any transform.</summary>
        public double[] Observed;
        public double Sigma;
    }

    public sealed class CaptureSolution
    {
        /// <summary>Column-major 4x4, capture frame to point-set frame.</summary>
        public double[] Matrix;
        public double Scale;
        public double RmsError;
        public double MaxError;
        public string AccuracyGrade;
        public string[] OutlierPointIds;
        /// <summary>True when this app solved it rather than reading it.</summary>
        public bool SolvedLocally;
    }

    public sealed class CaptureFile
    {
        public string ContractVersion;
        public string CaptureId;
        public string CapturedUtc;
        public string PointSetId;
        public string DeviceModel;
        public bool DeviceHasLidar;
        public List<CaptureCorrespondence> Correspondences;
        /// <summary>Null when the phone could not solve. Not an error.</summary>
        public CaptureSolution Solution;
        public string GeometryFile;
        public long GeometryBytes;
        /// <summary>Carried from the point set the capture was taken against.</summary>
        public double[] AppliedOffset;
        public string TargetUnits;
        public string UpAxis;
        public string SourceDocument;

        public bool HasSolution { get { return Solution != null && Solution.Matrix != null; } }
        public bool HasGeometry { get { return !string.IsNullOrEmpty(GeometryFile); } }
    }

    public class CaptureReadException : Exception
    {
        public CaptureReadException(string message) : base(message) { }
    }

    public static class CaptureReader
    {
        public const int SupportedMajorVersion = 1;

        /// <summary>
        /// Parse a capture.json. Throws with a line that can be shown verbatim.
        /// </summary>
        public static CaptureFile Read(string json)
        {
            JsonValue root;
            try
            {
                root = JsonReader.Parse(json);
            }
            catch (JsonParseException e)
            {
                throw new CaptureReadException("capture.json could not be read: " + e.Message);
            }

            if (root == null || root.Type != JsonValue.Kind.Object)
                throw new CaptureReadException("capture.json is not a JSON object.");

            // Version before anything else, so a future file produces the version
            // message rather than a confusing complaint about whichever field
            // happened to change.
            JsonValue versionValue = root["contractVersion"];
            string version = versionValue == null ? null : versionValue.AsString(null);
            if (string.IsNullOrEmpty(version))
                throw new CaptureReadException("capture.json has no contractVersion field.");
            int major = MajorVersion(version);
            if (major != SupportedMajorVersion)
                throw new CaptureReadException(
                    "capture.json is contract version " + version + "; this plugin reads version " +
                    SupportedMajorVersion + ".x. Update PIXMYD-Nav or re-export from a matching PIXMYD.");

            var file = new CaptureFile
            {
                ContractVersion = version,
                CaptureId = Str(root["captureId"], ""),
                CapturedUtc = Str(root["capturedUtc"], ""),
                PointSetId = Str(root["pointSetId"], ""),
                Correspondences = new List<CaptureCorrespondence>(),
                GeometryFile = "",
                AppliedOffset = new double[] { 0, 0, 0 },
                TargetUnits = "Meters",
                UpAxis = "Z",
                SourceDocument = ""
            };

            JsonValue device = root["device"];
            if (device != null)
            {
                file.DeviceModel = Str(device["model"], "");
                file.DeviceHasLidar = device["hasLidar"] != null && device["hasLidar"].AsBool(false);
            }

            JsonValue correspondences = root["correspondences"];
            if (correspondences != null && correspondences.Type == JsonValue.Kind.Array)
            {
                for (int i = 0; i < correspondences.Count; i++)
                {
                    JsonValue entry = correspondences.At(i);
                    if (entry == null) continue;
                    string id = Str(entry["pointId"], "");
                    double[] observed = entry["observed"] == null ? null : entry["observed"].AsVector(3);
                    if (string.IsNullOrEmpty(id) || observed == null) continue;
                    file.Correspondences.Add(new CaptureCorrespondence
                    {
                        PointId = id,
                        Observed = observed,
                        Sigma = entry["sigma"] == null ? 0 : entry["sigma"].AsNumber(0)
                    });
                }
            }

            JsonValue solution = root["solution"];
            if (solution != null && solution.Type == JsonValue.Kind.Object)
            {
                double[] matrix = solution["matrix"] == null ? null : solution["matrix"].AsVector(16);
                if (matrix != null)
                {
                    file.Solution = new CaptureSolution
                    {
                        Matrix = matrix,
                        Scale = solution["scale"] == null ? 1.0 : solution["scale"].AsNumber(1.0),
                        RmsError = solution["rmsError"] == null ? 0 : solution["rmsError"].AsNumber(0),
                        MaxError = solution["maxError"] == null ? 0 : solution["maxError"].AsNumber(0),
                        AccuracyGrade = Str(solution["accuracyGrade"], ""),
                        OutlierPointIds = solution["outlierPointIds"] == null
                            ? new string[0]
                            : solution["outlierPointIds"].AsStringArray(),
                        SolvedLocally = false
                    };
                }
            }

            JsonValue geometry = root["geometry"];
            if (geometry != null)
            {
                file.GeometryFile = Str(geometry["file"], "");
                file.GeometryBytes = (long)(geometry["bytes"] == null ? 0 : geometry["bytes"].AsNumber(0));
            }

            JsonValue provenance = root["provenance"];
            if (provenance != null)
            {
                double[] offset = provenance["navex:appliedOffset"] == null
                    ? null
                    : provenance["navex:appliedOffset"].AsVector(3);
                if (offset != null) file.AppliedOffset = offset;
                file.TargetUnits = Str(provenance["navex:targetUnits"], "Meters");
                file.UpAxis = Str(provenance["navex:upAxis"], "Z");
                file.SourceDocument = Str(provenance["navex:sourceDocument"], "");
            }

            return file;
        }

        /// <summary>
        /// Solve from the raw correspondences, given the point set's own
        /// coordinates. The contract's documented degraded mode.
        /// </summary>
        public static CaptureSolution SolveLocally(
            CaptureFile file,
            Dictionary<string, double[]> pointPositions)
        {
            if (file == null || file.Correspondences == null)
                throw new CaptureReadException("There are no observations in this capture to solve from.");
            if (pointPositions == null || pointPositions.Count == 0)
                throw new CaptureReadException(
                    "The point set for this capture is not loaded, so there is nothing to solve against.");

            var pairs = new List<ControlPair>();
            var unknown = new List<string>();
            foreach (CaptureCorrespondence c in file.Correspondences)
            {
                double[] project;
                if (!pointPositions.TryGetValue(c.PointId, out project))
                {
                    unknown.Add(c.PointId);
                    continue;
                }
                var pair = new ControlPair(c.PointId, project, c.Observed);
                pair.Sigma = c.Sigma;
                pairs.Add(pair);
            }

            if (pairs.Count == 0)
                throw new CaptureReadException(
                    unknown.Count == 0
                        ? "This capture has no observations that match the point set."
                        : "None of this capture's points are in the set: " + string.Join(", ", unknown.ToArray()) + ".");

            RigidSolution solved = RigidSolve.Solve(pairs);
            return new CaptureSolution
            {
                Matrix = solved.Matrix,
                Scale = solved.Scale,
                RmsError = solved.RmsError,
                MaxError = solved.MaxError,
                AccuracyGrade = AccuracyBands.Classify(solved.RmsError).Band,
                OutlierPointIds = new string[0],
                SolvedLocally = true
            };
        }

        private static string Str(JsonValue value, string fallback)
        {
            return value == null ? fallback : value.AsString(fallback);
        }

        internal static int MajorVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return -1;
            int dot = raw.IndexOf('.');
            string head = dot < 0 ? raw : raw.Substring(0, dot);
            int value;
            return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : -1;
        }
    }

    /// <summary>
    /// The construction tolerance bands, matching PIXMYD's classifyAccuracy so
    /// both ends of the contract call the same fit by the same name.
    ///
    /// These are working tolerances from the field, not a statistical
    /// convention. A number displayed without one is an unfinished measurement,
    /// because a crew will build to whatever is on the screen.
    /// </summary>
    public sealed class AccuracyGrade
    {
        public string Band;
        public string Label;
        public string Guidance;
        /// <summary>
        /// False below dimensional control. The contract requires explicit
        /// confirmation before placing anything graded worse than this.
        /// </summary>
        public bool WithinSurveyTolerance;
    }

    public static class AccuracyBands
    {
        public static AccuracyGrade Classify(double rmsError)
        {
            if (double.IsNaN(rmsError) || double.IsInfinity(rmsError) || rmsError < 0)
                return Grade("unusable", "No solution",
                    "The registration did not solve. Do not use this positioning for anything.", false);

            if (rmsError <= 0.003)
                return Grade("layout", "Layout",
                    "Within structural and MEP point layout tolerance (~3 mm).", true);

            if (rmsError <= 0.006)
                return Grade("penetrations", "Sleeves and penetrations",
                    "Good enough to locate sleeves and penetrations (~6 mm).", true);

            if (rmsError <= 0.010)
                return Grade("dimensional-control", "Dimensional control",
                    "Usable for dimensional control (~10 mm). Check anything tighter against an instrument.", true);

            if (rmsError <= 0.050)
                return Grade("coordination", "Coordination",
                    "Coordination-grade only (~50 mm). Do not lay out or fabricate from this.", false);

            if (rmsError <= 0.250)
                return Grade("context", "Context only",
                    "Context only (~250 mm). It shows roughly where things are and nothing more.", false);

            return Grade("unusable", "Unusable",
                "The fit is worse than a quarter of a metre. Re-locate the control points.", false);
        }

        private static AccuracyGrade Grade(string band, string label, string guidance, bool within)
        {
            return new AccuracyGrade
            {
                Band = band,
                Label = label,
                Guidance = guidance,
                WithinSurveyTolerance = within
            };
        }
    }

    /// <summary>
    /// Turning a solved capture into a placement in model world coordinates.
    ///
    /// Two steps, in this order, per capture.md: apply solution.matrix, then add
    /// appliedOffset from the point set's provenance. The offset is what the
    /// exporter subtracted to move the model to a local origin, so adding it back
    /// is what returns a coordinate to the source document's world space.
    /// </summary>
    public static class CapturePlacement
    {
        /// <summary>
        /// Map a point from the capture frame into model world coordinates.
        /// The matrix is column-major: element [c * 4 + r] is row r of column c.
        /// </summary>
        public static double[] ToModelWorld(double[] matrix, double[] appliedOffset, double[] p)
        {
            double[] mapped = Transform(matrix, p);
            if (appliedOffset == null || appliedOffset.Length != 3) return mapped;
            return new double[]
            {
                mapped[0] + appliedOffset[0],
                mapped[1] + appliedOffset[1],
                mapped[2] + appliedOffset[2]
            };
        }

        public static double[] Transform(double[] m, double[] p)
        {
            if (m == null || m.Length != 16 || p == null || p.Length != 3) return p;
            return new double[]
            {
                m[0] * p[0] + m[4] * p[1] + m[8]  * p[2] + m[12],
                m[1] * p[0] + m[5] * p[1] + m[9]  * p[2] + m[13],
                m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]
            };
        }

        /// <summary>
        /// The full transform to hand a placement API: the capture-to-model
        /// matrix with the origin offset folded into its translation column.
        /// </summary>
        public static double[] ModelWorldMatrix(double[] matrix, double[] appliedOffset)
        {
            if (matrix == null || matrix.Length != 16) return null;
            var result = (double[])matrix.Clone();
            if (appliedOffset != null && appliedOffset.Length == 3)
            {
                result[12] += appliedOffset[0];
                result[13] += appliedOffset[1];
                result[14] += appliedOffset[2];
            }
            return result;
        }
    }
}
