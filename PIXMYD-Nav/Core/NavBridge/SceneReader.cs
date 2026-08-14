using System;
using System.Globalization;
using Autodesk.Navisworks.Api;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// Reads the live Navisworks document into the PIXMYD export contracts.
    ///
    /// Everything here touches Autodesk.Navisworks.Api and must only be run from
    /// inside Navisworks. All reads are wrapped so a missing grid system, a
    /// viewpoint quirk or a hidden API change degrades to the contract defaults
    /// (empty strings, zero vectors) instead of failing the export.
    ///
    /// Grid note: the managed grid classes (GridsOptions, GridSystem,
    /// GridIntersection, PointInfo) are opaque in the 2025 API build -- they
    /// expose no public members at all. Grid intersection/level therefore come
    /// from the user, not from the model, and default to "" per docs/contracts
    /// /points.md (empty is the normal, documented case).
    /// </summary>
    public static class SceneReader
    {
        /// <summary>Everything about the current document we need for one export.</summary>
        public class SceneSnapshot
        {
            public Document Document;
            public Units SourceUnits;
            public double ScaleToMeters = 1.0;
            public string UpAxis = "Z";
            public Vec3 ModelMin;
            public Vec3 ModelMax;
            public CameraInfo Camera = new CameraInfo();
            public string SourceDocument = "";
            public string ModelName = "";
        }

        /// <summary>
        /// Snapshots the current document: units, up axis, whole-model bounding
        /// box (including hidden items -- AR anchoring needs the full extents,
        /// not what happens to be visible), and the current viewpoint camera.
        /// </summary>
        public static SceneSnapshot Capture(Document document)
        {
            var scene = new SceneSnapshot { Document = document, SourceUnits = Units.Meters };
            try { scene.SourceUnits = document.Units; } catch (Exception) { }

            try
            {
                // UnitConversion.ScaleFactor throws on unknown pairings; a bare
                // document in the middle of a file transition can surface that.
                scene.ScaleToMeters = UnitConversion.ScaleFactor(scene.SourceUnits, Units.Meters);
            }
            catch (Exception) { scene.ScaleToMeters = 1.0; }

            try
            {
                Vector3D up = document.UpVector;
                scene.UpAxis = Math.Abs(up.Z) > Math.Abs(up.Y) ? "Z" : "Y";
            }
            catch (Exception) { }

            try
            {
                BoundingBox3D box = document.GetBoundingBox(false);
                if (!box.IsEmpty && !double.IsNaN(box.Center.X))
                {
                    scene.ModelMin = Scale(ToVec(box.Min), scene.ScaleToMeters);
                    scene.ModelMax = Scale(ToVec(box.Max), scene.ScaleToMeters);
                }
            }
            catch (Exception) { }

            scene.Camera = CaptureCamera(document);

            try
            {
                scene.SourceDocument = document.Title ?? "";
                scene.ModelName = scene.SourceDocument;
            }
            catch (Exception) { }

            return scene;
        }

        /// <summary>Builds a point record for a selected model item, positioned at
        /// its bounding-box centre. Grid info is left for the user to fill in.</summary>
        public static PointRecord PointFromItem(Document document, ModelItem item, double scaleToMeters, string id)
        {
            var point = new PointRecord { Id = id };

            try
            {
                var items = new ModelItemCollection();
                items.Add(item);
                BoundingBox3D box = items.BoundingBox();
                if (!box.IsEmpty)
                    point.Position = Scale(ToVec(box.Center), scaleToMeters);
            }
            catch (Exception) { }

            try { point.Label = item.DisplayName; }
            catch (Exception) { point.Label = id; }

            return point;
        }

        /// <summary>
        /// Reads the current viewpoint camera. Navisworks exposes no managed
        /// screenshot API (verified against 2025.0.0), so the image is captured
        /// separately by <see cref="ViewportCapture"/>.
        /// </summary>
        public static CameraInfo CaptureCamera(Document document)
        {
            var camera = new CameraInfo();
            try
            {
                Viewpoint vp = document.CurrentViewpoint.Value;

                if (vp.Position != null)
                {
                    Point3D pos = vp.Position;
                    camera.Position = ToVec(pos);
                }

                try
                {
                    // The interop camera carries the view direction; look-at is
                    // position + direction, which is all the PIXMYD app needs.
                    Autodesk.Navisworks.Api.Interop.LcNvCamera cam = vp.InternalViewpoint.GetCamera();
                    Vector3D dir = cam.ViewDir();
                    camera.LookAt = new Vec3(
                        camera.Position.X + dir.X,
                        camera.Position.Y + dir.Y,
                        camera.Position.Z + dir.Z);
                }
                catch (Exception) { }

                if (vp.WorldUpVector != null)
                {
                    Vector3D wup = vp.WorldUpVector.ToVector3D();
                    if (Math.Abs(wup.X) + Math.Abs(wup.Y) + Math.Abs(wup.Z) > 0)
                        camera.UpVector = ToVec(wup);
                }

                // Vertical field of view from the extent-at-focal-distance pair.
                if (vp.HasFocalDistance && vp.FocalDistance > 0)
                {
                    double half = vp.VerticalExtentAtFocalDistance * 0.5 / vp.FocalDistance;
                    half = Math.Min(1.0, Math.Max(0.001, half));
                    camera.FovDegrees = 2.0 * Math.Atan(half) * 180.0 / Math.PI;
                    if (camera.FovDegrees <= 0 || camera.FovDegrees > 179) camera.FovDegrees = 45.0;
                }
            }
            catch (Exception) { }
            return camera;
        }

        /// <summary>Positioned at the centre of the current selection -- the
        /// natural spot for a survey point.</summary>
        public static bool TrySelectionCenter(Document document, out Vec3 center)
        {
            center = new Vec3();
            try
            {
                Selection selection = document.CurrentSelection;
                if (!selection.HasExplicitSelection) return false;
                ModelItemCollection sel = selection.ExplicitSelection;
                if (sel.Count == 0) return false;

                var parts = new ModelItemCollection();
                foreach (ModelItem item in sel) { try { if (item.HasGeometry) parts.Add(item); } catch (Exception) { } }

                if (parts.Count == 0) return false;
                BoundingBox3D box = parts.BoundingBox();
                if (box.IsEmpty) return false;
                center = ToVec(box.Center);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Vec3 ToVec(Point3D p)
        {
            if (p == null) return new Vec3();
            return new Vec3(p.X, p.Y, p.Z);
        }

        private static Vec3 ToVec(Vector3D v)
        {
            if (v == null) return new Vec3();
            return new Vec3(v.X, v.Y, v.Z);
        }

        private static Vec3 Scale(Vec3 v, double factor)
        {
            if (factor == 1.0) return v;
            return new Vec3(v.X * factor, v.Y * factor, v.Z * factor);
        }

        public static string FormatVec(Vec3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.000}, {1:0.000}, {2:0.000}", v.X, v.Y, v.Z);
        }
    }
}