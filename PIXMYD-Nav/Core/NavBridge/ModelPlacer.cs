using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.Navisworks.Api;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// Putting things into the open document, and reading back what the user
    /// moved.
    ///
    /// The managed API cannot author geometry -- that has been true since the
    /// first version of this plugin said so -- but it can append a file and it
    /// can transform what it appended. Those two together are enough for both
    /// halves of this feature:
    ///
    /// * the placed points become a DXF that is appended, so each one is a real
    ///   solid on its own named layer, selectable in the tree and present in
    ///   anything the model is exported to;
    /// * an arriving scan becomes an appended FBX, and its transform is the
    ///   alignment -- so refining the alignment is setting a transform, not
    ///   re-importing anything.
    ///
    /// ## The gizmo
    ///
    /// Navisworks already has a move gizmo, with snapping, in Item Tools. It
    /// works on appended geometry, which is what a marker now is. So there is
    /// no imitation gizmo here: the user drags the marker with the application's
    /// own tool and presses Read back moves, and
    /// <see cref="ReadMoves"/> turns the override transform Navisworks recorded
    /// into a new coordinate for that point. The plugin's nudge buttons cover
    /// the millimetre case where dragging is the wrong instrument.
    ///
    /// Read-back is idempotent by remembering what it has already consumed --
    /// pressing the button twice must not move a point twice.
    ///
    /// Navisworks-only.
    /// </summary>
    public sealed class ModelPlacer
    {
        /// <summary>Override translations already folded into point coordinates,
        /// by point id, in document units.</summary>
        private readonly Dictionary<string, double[]> _consumed =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

        public sealed class AppendResult
        {
            public bool Ok;
            public Model Model;
            public string Message = "";
        }

        /// <summary>
        /// Append a file and hand back the Model it became.
        ///
        /// Navisworks does not report which model an append produced, so the
        /// list is compared before and after. Comparing counts alone would be
        /// wrong for a file that appends as several models, so the new entries
        /// are found by identity.
        /// </summary>
        public static AppendResult Append(Document document, string path)
        {
            var result = new AppendResult();
            if (document == null) { result.Message = "No document is open."; return result; }
            if (!File.Exists(path)) { result.Message = "There is no file at " + path + "."; return result; }

            var before = new List<Model>();
            try { foreach (Model model in document.Models) before.Add(model); }
            catch (Exception) { }

            try
            {
                if (!document.TryAppendFile(path))
                {
                    result.Message =
                        "Navisworks would not append " + Path.GetFileName(path) + ". The reader for " +
                        "that format may not be installed in this edition.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Message = "Append failed: " + ex.Message;
                return result;
            }

            try
            {
                foreach (Model model in document.Models)
                {
                    if (before.Contains(model)) continue;
                    result.Model = model;
                    break;
                }
            }
            catch (Exception) { }

            result.Ok = true;
            result.Message = result.Model == null
                ? "Appended " + Path.GetFileName(path) + "."
                : "Appended " + Path.GetFileName(path) + " as a new model.";
            return result;
        }

        /// <summary>The appended model that came from this file, or null.</summary>
        public static Model FindByFile(Document document, string path)
        {
            if (document == null || string.IsNullOrEmpty(path)) return null;
            string wanted = Path.GetFullPath(path);
            try
            {
                foreach (Model model in document.Models)
                {
                    string name = model.SourceFileName;
                    if (string.IsNullOrEmpty(name)) name = model.FileName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (string.Equals(Path.GetFullPath(name), wanted, StringComparison.OrdinalIgnoreCase))
                        return model;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Move an appended model by a column-major 4x4, in the document's own
        /// units.
        ///
        /// The matrix is decomposed to an axis, an angle and a translation
        /// before it crosses into Navisworks -- see
        /// Core/Capture/TransformMath.cs for why that spelling and not the 3x3
        /// one.
        /// </summary>
        public static bool TryTransform(Document document, Model model, double[] columnMajor16, out string error)
        {
            error = "";
            if (document == null || model == null) { error = "There is no model to place."; return false; }

            TransformMath.Decomposed parts = TransformMath.Decompose(columnMajor16);
            if (parts == null)
            {
                error = "That alignment is not a usable transform, so nothing was moved.";
                return false;
            }

            Transform3D transform;
            try
            {
                var axis = new UnitVector3D(parts.Axis[0], parts.Axis[1], parts.Axis[2]);
                var rotation = new Rotation3D(axis, parts.AngleRadians);
                var translation = new Vector3D(parts.Translation[0], parts.Translation[1], parts.Translation[2]);
                transform = new Transform3D(rotation, translation);
            }
            catch (Exception ex)
            {
                error = "Could not build the placement transform: " + ex.Message;
                return false;
            }

            try
            {
                // The model's own units are passed back unchanged: this call
                // sets units and transform together, and the units are not what
                // is being changed here.
                document.Models.SetModelUnitsAndTransform(model, model.Units, transform, false);
                return true;
            }
            catch (Exception first)
            {
                try
                {
                    // Older builds refuse the model-level call for a model the
                    // user has already transformed by hand. Overriding the root
                    // item reaches the same geometry.
                    var items = new ModelItemCollection();
                    items.Add(model.RootItem);
                    document.Models.OverridePermanentTransform(items, transform, false);
                    return true;
                }
                catch (Exception second)
                {
                    error = "Navisworks refused to place the scan: " + first.Message +
                            "  (and, on the item path: " + second.Message + ")";
                    return false;
                }
            }
        }

        /// <summary>
        /// What the user moved since the last read, by point id, in metres.
        ///
        /// Markers are found by the layer name the DXF writer gave them, which
        /// is how a point id survives the round trip through a file format --
        /// see Core/Markers/MarkerGeometry.cs. Anything with no override, or
        /// with the override already consumed, contributes nothing.
        /// </summary>
        public Dictionary<string, Vec3> ReadMoves(Model markerModel, IList<string> pointIds, double scaleToMeters)
        {
            var moves = new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);
            if (markerModel == null || pointIds == null) return moves;

            Dictionary<string, ModelItem> byLayer = LayerNodes(markerModel);

            foreach (string id in pointIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                ModelItem node;
                if (!byLayer.TryGetValue(MarkerDxf.LayerNameFor(id), out node)) continue;

                double[] total = OverrideTranslation(node);
                if (total == null) continue;

                double[] already;
                if (!_consumed.TryGetValue(id, out already)) already = new double[] { 0, 0, 0 };

                double dx = total[0] - already[0];
                double dy = total[1] - already[1];
                double dz = total[2] - already[2];
                _consumed[id] = total;

                // Sub-micron moves are the user brushing the gizmo, not an
                // adjustment worth rewriting a survey coordinate for.
                if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) < 1e-9) continue;

                moves[id] = new Vec3(dx * scaleToMeters, dy * scaleToMeters, dz * scaleToMeters);
            }

            return moves;
        }

        /// <summary>Forget what has been consumed, so a freshly appended marker
        /// set starts from zero.</summary>
        public void ForgetMoves() { _consumed.Clear(); }

        private static Dictionary<string, ModelItem> LayerNodes(Model model)
        {
            var found = new Dictionary<string, ModelItem>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ModelItem item in model.RootItem.DescendantsAndSelf)
                {
                    string name = item.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(MarkerDxf.LayerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.ContainsKey(name)) found[name] = item;
                }
            }
            catch (Exception) { }
            return found;
        }

        /// <summary>
        /// The override translation on a subtree, or null when nothing in it has
        /// been moved. Takes the first one found: the user drags a whole marker,
        /// and every facet of it carries the same override.
        /// </summary>
        private static double[] OverrideTranslation(ModelItem node)
        {
            try
            {
                foreach (ModelItem item in node.DescendantsAndSelf)
                {
                    if (!item.HasGeometry) continue;
                    ModelGeometry geometry = item.Geometry;
                    if (geometry == null) continue;

                    Transform3D over = geometry.PermanentOverrideTransform;
                    if (over == null || over.IsIdentity()) continue;

                    Vector3D t = over.Translation;
                    return new double[] { t.X, t.Y, t.Z };
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Select the marker for a point, so the user can reach for the
        /// move gizmo without hunting the tree for it.</summary>
        public static bool TrySelectMarker(Document document, Model markerModel, string pointId)
        {
            if (document == null || markerModel == null || string.IsNullOrEmpty(pointId)) return false;
            try
            {
                ModelItem node;
                if (!LayerNodes(markerModel).TryGetValue(MarkerDxf.LayerNameFor(pointId), out node)) return false;

                var items = new ModelItemCollection();
                items.Add(node);
                document.CurrentSelection.CopyFrom(items);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static string Describe(Vec3 v)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:0.000}, {1:0.000}, {2:0.000}", v.X, v.Y, v.Z);
        }
    }
}
