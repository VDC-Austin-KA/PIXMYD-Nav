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
        /// <summary>
        /// True when the operator placed this by hand rather than solving it.
        ///
        /// A hand placement has no residuals, so <see cref="RmsError"/> is zero
        /// -- and zero is the number an excellent fit reports. Anything that
        /// grades or quotes the error has to ask this first, or the least
        /// trustworthy placement in the suite is the one that reads best.
        /// </summary>
        public bool NotMeasured;
        /// <summary>True when the solve held the vertical from gravity rather
        /// than fitting all six degrees of freedom.</summary>
        public bool VerticalHeld;
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
        /// <summary>
        /// Which frame the mesh is written in: "model" when the phone baked the
        /// alignment into the geometry before sending it, "capture" when it is
        /// raw. Defaults to "capture", which is what every file written before
        /// this field existed contains.
        ///
        /// It matters because a mesh already in model coordinates is appended
        /// and left alone, and a raw one has to be transformed. Applying the
        /// transform twice puts the scan exactly as far past the model as it
        /// was short of it, which looks like a solver bug and is not.
        /// </summary>
        public string GeometryFrame = "capture";
        /// <summary>The up axis of the capture's own frame. ARKit is Y-up.</summary>
        public string CaptureUpAxis = "Y";
        /// <summary>Points the phone placed itself, when it sent any.</summary>
        public string FieldPointsFile = "";
        /// <summary>Carried from the point set the capture was taken against.</summary>
        public double[] AppliedOffset;
        public string TargetUnits;
        public string UpAxis;
        public string SourceDocument;

        public bool HasSolution { get { return Solution != null && Solution.Matrix != null; } }
        public bool HasGeometry { get { return !string.IsNullOrEmpty(GeometryFile); } }

        /// <summary>True when the mesh is already in model world coordinates.</summary>
        public bool GeometryIsPlaced
        {
            get { return string.Equals(GeometryFrame, "model", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Whether Navisworks can append this mesh at all.
        ///
        /// FBX is what the phone writes now, because appending a file is the
        /// only way a plugin can put geometry into an open document and FBX is a
        /// format Navisworks reads without an extra exporter. Captures from
        /// older builds carry a .glb, which Navisworks does not read -- that is
        /// worth saying plainly rather than failing at the append.
        /// </summary>
        public bool GeometryIsAppendable
        {
            get
            {
                if (!HasGeometry) return false;
                string extension = System.IO.Path.GetExtension(GeometryFile);
                // OBJ is not in this list on purpose: Navisworks does not read
                // it, and the plugin converts it to NWC first. "Appendable"
                // here means appendable as it stands.
                return string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".dwg", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".dxf", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".stl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".nwc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".nwd", StringComparison.OrdinalIgnoreCase);
            }
        }
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
                file.GeometryFrame = Str(geometry["frame"], "capture");
            }

            JsonValue fieldPoints = root["fieldPoints"];
            if (fieldPoints != null) file.FieldPointsFile = Str(fieldPoints["file"], "");

            JsonValue provenance = root["provenance"];
            if (provenance != null)
            {
                double[] offset = provenance["navex:appliedOffset"] == null
                    ? null
                    : provenance["navex:appliedOffset"].AsVector(3);
                if (offset != null) file.AppliedOffset = offset;
                file.TargetUnits = Str(provenance["navex:targetUnits"], "Meters");
                file.UpAxis = Str(provenance["navex:upAxis"], "Z");
                file.CaptureUpAxis = Str(provenance["pixmyd:captureUpAxis"], "Y");
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

            return Solve(pairs, file.CaptureUpAxis, file.UpAxis, false);
        }

        /// <summary>
        /// Solve a set of pairs, choosing the method the data supports.
        ///
        /// Three or more pairs get Horn's unconstrained solve, which is what the
        /// contract's numbers have always meant. Two get the gravity-constrained
        /// one: both frames know which way down is, so holding the vertical
        /// leaves four unknowns that two points over-determine. Below two there
        /// is nothing to do, and the refusal says so.
        ///
        /// <paramref name="forceGravity"/> is for the case where the operator
        /// knows better than the residuals -- three hand-aimed picks fit a tilt
        /// more readily than an IMU gets gravity wrong, and holding the vertical
        /// is often the better answer even when Horn's is available.
        /// </summary>
        public static CaptureSolution Solve(
            List<ControlPair> pairs,
            string captureUpAxis,
            string projectUpAxis,
            bool forceGravity)
        {
            bool gravity = forceGravity || pairs.Count < 3;
            RigidSolution solved = gravity
                ? GravitySolve.Solve(
                    pairs,
                    GravitySolve.UpVectorFor(string.IsNullOrEmpty(captureUpAxis) ? "Y" : captureUpAxis),
                    GravitySolve.UpVectorFor(projectUpAxis))
                : RigidSolve.Solve(pairs);

            return new CaptureSolution
            {
                Matrix = solved.Matrix,
                Scale = solved.Scale,
                RmsError = solved.RmsError,
                MaxError = solved.MaxError,
                AccuracyGrade = AccuracyBands.Classify(solved.RmsError).Band,
                OutlierPointIds = new string[0],
                SolvedLocally = true,
                VerticalHeld = gravity
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
        /// <summary>
        /// A placement with no solve behind it: no turn, sitting at an anchor.
        ///
        /// For the case the suite had no answer to at all -- a capture whose
        /// points do not match the model's, or that was taken with no points.
        /// Refusing to place it leaves the operator with a scan they cannot
        /// see; putting it where they are looking, unrotated, and saying it is
        /// not measured leaves them with something they can nudge into place
        /// and a number that does not pretend otherwise.
        /// </summary>
        /// <param name="anchorMetres">Where to put it, model world, metres.</param>
        /// <summary>
        /// A placement with no fit behind it, at <paramref name="anchorMetres"/>.
        ///
        /// Not unrotated, which is what this used to be and what put every
        /// hand-placed scan on its side. A solution matrix maps the capture's
        /// own frame to model world, and the capture's frame is ARKit's Y-up
        /// one -- so the identity is not "no rotation", it is the claim that a
        /// Y-up capture is already Z-up, which is false in exactly the way you
        /// notice from across the room.
        ///
        /// There is no fit here, so there is no heading to know: the operator
        /// yaws it into place. What there is, always, is which way is up, and
        /// that costs nothing to get right.
        ///
        /// <paramref name="documentUpAxis"/> is "Z" for an ordinary Navisworks
        /// document. A Y-up document already agrees with the capture, so the
        /// turn is the identity there and the old behaviour is what it gets.
        /// </summary>
        public static CaptureSolution ByHand(double[] anchorMetres, string documentUpAxis)
        {
            double[] a = anchorMetres != null && anchorMetres.Length == 3
                ? anchorMetres
                : new double[] { 0, 0, 0 };

            // The same quarter turn about +X the converter bakes into the mesh
            // and TransformMath.FbxCaptureBasis takes back out, composed here
            // rather than written as literals so the three cannot drift apart.
            bool documentIsYUp = string.Equals(
                (documentUpAxis ?? "Z").Trim(), "Y", StringComparison.OrdinalIgnoreCase);
            double[] turn = TransformMath.Compose(
                new double[] { 1, 0, 0 }, documentIsYUp ? 0 : Math.PI * 0.5, a);

            return new CaptureSolution
            {
                Matrix = turn,
                Scale = 1,
                RmsError = 0,
                MaxError = 0,
                OutlierPointIds = new string[0],
                SolvedLocally = false,
                NotMeasured = true
            };
        }

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
