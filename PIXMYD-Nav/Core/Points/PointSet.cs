using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PIXMYD_Nav.Core.Points
{
    /// <summary>A 3D vector in the exported coordinate frame.</summary>
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Nearest grid intersection to a point. Empty strings, never null,
    /// when no grid system is loaded -- that is the normal case, not an error.</summary>
    public class GridInfo
    {
        public string Intersection = "";
        public string Level = "";
        public Vec3 Offset;
        public double Distance;
    }

    public class CameraInfo
    {
        public Vec3 Position;
        public Vec3 LookAt;
        public Vec3 UpVector = new Vec3(0, 0, 1);
        public double FovDegrees = 45.0;
    }

    /// <summary>Optional -- absent until viewpoint/image capture (deferred, see P1 in
    /// docs/work-orders/pixmy4d-nav.md) fills it in.</summary>
    public class ViewpointInfo
    {
        public string Image = "";
        public string ThumbMono; // null omits the field per the contract
        public CameraInfo Camera = new CameraInfo();
    }

    public class PointRecord
    {
        public string Id;
        public string Label = "";
        public Vec3 Position;
        public GridInfo Grid = new GridInfo();
        public ViewpointInfo Viewpoint;
    }

    public class Provenance
    {
        public string SourceDocument = "";
        public string SourceUnits = "";
        public string TargetUnits = "";
        public string UpAxis = "";
        public string OriginMode = "";
        public Vec3 AppliedOffset;
        public string OffsetNote =
            "Add appliedOffset to exported coordinates to return to source world coordinates.";
        public DateTime ExportedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// A named set of surveyed points, plus the hand-rolled JSON writer that
    /// serialises it to points.json per docs/contracts/points.md. No JSON library --
    /// mirrors NavEx's approach in Core/Exporters/Json.cs so the plugin stays a
    /// single dependency-free DLL.
    /// </summary>
    public class PointSet
    {
        public const string ContractVersion = "1.0";

        public string SetId = Guid.NewGuid().ToString();
        public string SetName = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public Provenance Provenance = new Provenance();
        public List<PointRecord> Points = new List<PointRecord>();

        /// <summary>pixmy://p/&lt;first 8 chars of setId&gt;/&lt;pointId&gt; -- computed,
        /// never stored separately, so it can never drift from setId.</summary>
        public string QrPayloadFor(PointRecord point)
        {
            string shortSetId = SetId.Length >= 8 ? SetId.Substring(0, 8) : SetId;
            return "pixmy://p/" + shortSetId + "/" + point.Id;
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            BuildRoot().Write(sb);
            return sb.ToString();
        }

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        private JObj BuildRoot()
        {
            var root = new JObj();
            root.Set("contractVersion", ContractVersion);
            root.Set("setId", SetId);
            root.Set("setName", SetName);
            root.Set("createdUtc", FormatUtc(CreatedUtc));
            root.Set("provenance", BuildProvenance());

            var points = new JArr();
            foreach (PointRecord point in Points) points.Add(BuildPoint(point));
            root.Set("points", points);

            return root;
        }

        private JObj BuildProvenance()
        {
            var p = Provenance;
            return new JObj()
                .Set("navex:sourceDocument", p.SourceDocument)
                .Set("navex:sourceUnits", p.SourceUnits)
                .Set("navex:targetUnits", p.TargetUnits)
                .Set("navex:upAxis", p.UpAxis)
                .Set("navex:originMode", p.OriginMode)
                .Set("navex:appliedOffset", Vec(p.AppliedOffset))
                .Set("navex:offsetNote", p.OffsetNote)
                .Set("navex:exportedUtc", FormatUtc(p.ExportedUtc));
        }

        private JObj BuildPoint(PointRecord point)
        {
            var obj = new JObj()
                .Set("id", point.Id)
                .Set("label", point.Label)
                .Set("position", Vec(point.Position))
                .Set("grid", BuildGrid(point.Grid));

            if (point.Viewpoint != null)
                obj.Set("viewpoint", BuildViewpoint(point.Viewpoint));

            obj.Set("qrPayload", QrPayloadFor(point));
            return obj;
        }

        private static JObj BuildGrid(GridInfo grid)
        {
            return new JObj()
                .Set("intersection", grid.Intersection ?? "")
                .Set("level", grid.Level ?? "")
                .Set("offset", Vec(grid.Offset))
                .Set("distance", grid.Distance);
        }

        private static JObj BuildViewpoint(ViewpointInfo vp)
        {
            var obj = new JObj().Set("image", vp.Image ?? "");
            if (!string.IsNullOrEmpty(vp.ThumbMono))
                obj.Set("thumbMono", vp.ThumbMono);
            obj.Set("camera", new JObj()
                .Set("position", Vec(vp.Camera.Position))
                .Set("lookAt", Vec(vp.Camera.LookAt))
                .Set("upVector", Vec(vp.Camera.UpVector))
                .Set("fovDegrees", vp.Camera.FovDegrees));
            return obj;
        }

        private static JArr Vec(Vec3 v) { return JArr.Of(v.X, v.Y, v.Z); }

        private static string FormatUtc(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
    }

    // ---- Minimal hand-rolled JSON DOM, mirroring NavEx/Core/Exporters/Json.cs ----

    internal abstract class JVal
    {
        public abstract void Write(StringBuilder sb);

        public static implicit operator JVal(string value) { return new JStr(value); }
        public static implicit operator JVal(double value) { return new JNum(value); }
        public static implicit operator JVal(int value) { return new JNum(value); }
        public static implicit operator JVal(bool value) { return new JBool(value); }
    }

    internal class JStr : JVal
    {
        private readonly string _value;
        public JStr(string value) { _value = value ?? ""; }

        public override void Write(StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in _value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    internal class JNum : JVal
    {
        private readonly double _value;
        public JNum(double value) { _value = value; }

        public override void Write(StringBuilder sb)
        {
            double value = _value;
            // JSON has no NaN or Infinity; a stray one would make the whole file
            // unreadable, so degrade to 0 rather than emit invalid output.
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;

            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    internal class JBool : JVal
    {
        private readonly bool _value;
        public JBool(bool value) { _value = value; }
        public override void Write(StringBuilder sb) { sb.Append(_value ? "true" : "false"); }
    }

    internal class JArr : JVal
    {
        private readonly List<JVal> _items = new List<JVal>();

        public JArr Add(JVal value) { _items.Add(value); return this; }

        public static JArr Of(params double[] values)
        {
            var array = new JArr();
            foreach (double v in values) array.Add(new JNum(v));
            return array;
        }

        public override void Write(StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                _items[i].Write(sb);
            }
            sb.Append(']');
        }
    }

    internal class JObj : JVal
    {
        private readonly List<KeyValuePair<string, JVal>> _members = new List<KeyValuePair<string, JVal>>();

        public JObj Set(string name, JVal value)
        {
            _members.Add(new KeyValuePair<string, JVal>(name, value));
            return this;
        }

        public override void Write(StringBuilder sb)
        {
            sb.Append('{');
            for (int i = 0; i < _members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                new JStr(_members[i].Key).Write(sb);
                sb.Append(':');
                _members[i].Value.Write(sb);
            }
            sb.Append('}');
        }
    }
}
