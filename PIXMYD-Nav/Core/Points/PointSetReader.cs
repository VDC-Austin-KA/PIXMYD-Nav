using System;
using System.Collections.Generic;
using System.Globalization;
using PIXMYD_Nav.Core.Json;

namespace PIXMYD_Nav.Core.Points
{
    /// <summary>
    /// Reading a points.json back in.
    ///
    /// The plugin has always written this file and never read one. It has to
    /// now, because points can start at the other end: a crew walking a space
    /// for the first time places control points on the phone, and the scan
    /// comes home with a points.json naming them. The workstation's job is then
    /// to put the same ids on the model, which it cannot do without knowing
    /// what they are called.
    ///
    /// The phone writes the same shape this plugin writes -- deliberately, so
    /// there is one schema and one version gate rather than two. What differs
    /// is the frame, and the file says so: `navex:originMode` is
    /// `CaptureOrigin` and `pixmyd:frame` is `capture`, meaning the coordinates
    /// are ARKit's and not any model's. Reading them as model coordinates would
    /// put a scan at the origin and look like a solver bug, so
    /// <see cref="ReadPointSet.IsCaptureFrame"/> is checked rather than assumed.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public sealed class ReadPoint
    {
        public string Id = "";
        public string Label = "";
        public Vec3 Position;
        /// <summary>"mesh", "plane" or "manual" when the producer said; empty
        /// otherwise.</summary>
        public string Source = "";
        /// <summary>How far the phone was from the point, when recorded.</summary>
        public double RangeMetres;
    }

    public sealed class ReadPointSet
    {
        public string ContractVersion = "";
        public string SetId = "";
        public string SetName = "";
        public string SourceDocument = "";
        public string OriginMode = "";
        public string Frame = "";
        public string UpAxis = "Z";
        public List<ReadPoint> Points = new List<ReadPoint>();

        /// <summary>
        /// True when these coordinates are a phone's capture frame rather than
        /// a model's. Not a detail: it decides whether the positions can be
        /// used directly or only as ids to place against.
        /// </summary>
        public bool IsCaptureFrame
        {
            get
            {
                return string.Equals(Frame, "capture", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(OriginMode, "CaptureOrigin", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string ShortId
        {
            get { return SetId.Length <= 8 ? SetId : SetId.Substring(0, 8); }
        }
    }

    public static class PointSetReader
    {
        public const int SupportedMajorVersion = 1;

        /// <summary>
        /// Parse a points.json. Throws with a line that can be shown verbatim,
        /// matching how CaptureReader behaves for the file beside it.
        /// </summary>
        public static ReadPointSet Read(string json)
        {
            JsonValue root;
            try
            {
                root = JsonReader.Parse(json);
            }
            catch (JsonParseException e)
            {
                throw new PointSetReadException("points.json could not be read: " + e.Message);
            }

            if (root == null || root.Type != JsonValue.Kind.Object)
                throw new PointSetReadException("points.json is not a JSON object.");

            // Version before anything else, so a future file produces the
            // version message rather than a confusing complaint about whichever
            // field happened to change.
            JsonValue versionValue = root["contractVersion"];
            string version = versionValue == null ? null : versionValue.AsString(null);
            if (string.IsNullOrEmpty(version))
                throw new PointSetReadException("points.json has no contractVersion field.");
            if (MajorVersion(version) != SupportedMajorVersion)
                throw new PointSetReadException(
                    "points.json is contract version " + version + "; this plugin reads version " +
                    SupportedMajorVersion + ".x. Update PIXMYD-Nav or re-export from a matching PIXMYD.");

            var set = new ReadPointSet
            {
                ContractVersion = version,
                SetId = Str(root["setId"], ""),
                SetName = Str(root["setName"], "")
            };

            JsonValue provenance = root["provenance"];
            if (provenance != null)
            {
                set.SourceDocument = Str(provenance["navex:sourceDocument"], "");
                set.OriginMode = Str(provenance["navex:originMode"], "");
                set.UpAxis = Str(provenance["navex:upAxis"], "Z");
                set.Frame = Str(provenance["pixmyd:frame"], "");
            }

            JsonValue points = root["points"];
            if (points != null && points.Type == JsonValue.Kind.Array)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    JsonValue entry = points.At(i);
                    if (entry == null) continue;

                    string id = Str(entry["id"], "");
                    if (string.IsNullOrEmpty(id)) continue;

                    double[] position = entry["position"] == null ? null : entry["position"].AsVector(3);
                    var point = new ReadPoint
                    {
                        Id = id,
                        Label = Str(entry["label"], id),
                        Source = Str(entry["pixmyd:source"], ""),
                        RangeMetres = entry["pixmyd:rangeMetres"] == null
                            ? 0
                            : entry["pixmyd:rangeMetres"].AsNumber(0)
                    };
                    if (position != null) point.Position = new Vec3(position[0], position[1], position[2]);
                    set.Points.Add(point);
                }
            }

            // An empty points array is explicitly valid -- the contract says to
            // render an empty state rather than treat it as a failure.
            return set;
        }

        /// <summary>One line describing what arrived, for a status bar.</summary>
        public static string Describe(ReadPointSet set)
        {
            if (set == null) return "";
            string name = string.IsNullOrEmpty(set.SetName) ? set.ShortId : set.SetName;
            string frame = set.IsCaptureFrame
                ? "in the phone's own frame — place the same ids on the model to register them"
                : "in model coordinates";
            return name + ": " + set.Points.Count.ToString(CultureInfo.InvariantCulture) +
                   " point(s), " + frame + ".";
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

    public class PointSetReadException : Exception
    {
        public PointSetReadException(string message) : base(message) { }
    }
}
