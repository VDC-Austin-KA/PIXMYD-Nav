using System;
using System.Globalization;
using System.IO;
using System.Text;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Ar
{
    /// <summary>
    /// AR Model Export: the whole-model bounding box and the capture camera, so
    /// the PIXMYD phone app can anchor the model on top of the real world.
    ///
    /// Contract: ar-model.json per docs/contracts (points.md conventions -- the
    /// hand-rolled JObj writer is shared with PointSet). Coordinates are in the
    /// target units (default meters), shifted by appliedOffset so the box starts
    /// near the origin; add appliedOffset back to reach source world coordinates.
    /// </summary>
    public class ArModelSet
    {
        public const string ContractVersion = "1.0";

        public string ModelId = Guid.NewGuid().ToString();
        public string ModelName = "";
        public DateTime CreatedUtc = DateTime.UtcNow;

        public string SourceDocument = "";
        public string SourceUnits = "";
        public string TargetUnits = "Meters";
        public string UpAxis = "Z";
        public string OriginMode = "ModelMin";
        public Vec3 AppliedOffset;
        public string OffsetNote =
            "Add appliedOffset to exported coordinates to return to source world coordinates.";

        public Vec3 BBoxMin;
        public Vec3 BBoxMax;

        public CameraInfo Camera = new CameraInfo();

        /// <summary>Relative file names next to ar-model.json ("" = no photo taken).</summary>
        public string Image = "";
        public string ThumbMono = "";

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
            root.Set("modelId", ModelId);
            root.Set("modelName", ModelName);
            root.Set("createdUtc", FormatUtc(CreatedUtc));

            var p = new JObj()
                .Set("navex:sourceDocument", SourceDocument)
                .Set("navex:sourceUnits", SourceUnits)
                .Set("navex:targetUnits", TargetUnits)
                .Set("navex:upAxis", UpAxis)
                .Set("navex:originMode", OriginMode)
                .Set("navex:appliedOffset", Vec(AppliedOffset))
                .Set("navex:offsetNote", OffsetNote)
                .Set("navex:exportedUtc", FormatUtc(CreatedUtc));
            root.Set("provenance", p);

            Vec3 center = new Vec3(
                (BBoxMin.X + BBoxMax.X) * 0.5,
                (BBoxMin.Y + BBoxMax.Y) * 0.5,
                (BBoxMin.Z + BBoxMax.Z) * 0.5);
            Vec3 size = new Vec3(
                BBoxMax.X - BBoxMin.X,
                BBoxMax.Y - BBoxMin.Y,
                BBoxMax.Z - BBoxMin.Z);

            root.Set("boundingBox", new JObj()
                .Set("min", Vec(BBoxMin))
                .Set("max", Vec(BBoxMax))
                .Set("center", Vec(center))
                .Set("size", Vec(size)));

            root.Set("camera", new JObj()
                .Set("position", Vec(Camera.Position))
                .Set("lookAt", Vec(Camera.LookAt))
                .Set("upVector", Vec(Camera.UpVector))
                .Set("fovDegrees", Camera.FovDegrees));

            root.Set("image", Image ?? "");
            root.Set("thumbMono", ThumbMono ?? "");

            return root;
        }

        private static JArr Vec(Vec3 v) { return JArr.Of(v.X, v.Y, v.Z); }

        private static string FormatUtc(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
    }
}