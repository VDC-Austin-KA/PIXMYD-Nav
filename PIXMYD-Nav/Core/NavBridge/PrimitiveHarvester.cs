using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// Real triangles and real edges out of a Navisworks document.
    ///
    /// The managed API exposes geometry as counts and bounding boxes, never as
    /// coordinates. The COM bridge does: <c>InwOaFragment3
    /// .GenerateSimplePrimitives</c> walks a fragment and calls back with each
    /// triangle, line, point and -- most usefully here -- each snap point the
    /// model itself publishes. It is the same door every Navisworks exporter
    /// goes through, and it is the only one there is.
    ///
    /// Two things this feeds:
    ///
    /// * **Snapping.** A pick lands somewhere near a corner; the corner is in
    ///   here. See Core/Points/SnapSolver.cs, which does the maths with no
    ///   Navisworks in scope so it can be tested.
    /// * **AR model export.** The phone needs geometry to draw, and until now
    ///   ar-model.json shipped a bounding box and an apology.
    ///
    /// ## Cost
    ///
    /// Generating primitives is not free -- it is a full tessellation walk of
    /// whatever is asked for. So nothing here ever walks the whole model
    /// implicitly. <see cref="ItemsNear"/> prunes by bounding box from the
    /// roots down, which is a real spatial index because a Navisworks group's
    /// box contains its children's, and every harvest takes a budget it stops
    /// at. A snap that consults four hundred triangles around one corner is
    /// instant; one that consults a stadium is a hang.
    ///
    /// Navisworks-only. Everything it produces is testable; nothing in this
    /// file is.
    /// </summary>
    public static class PrimitiveHarvester
    {
        /// <summary>How many geometry items one snap is allowed to tessellate.</summary>
        public const int DefaultItemBudget = 24;
        /// <summary>How many triangles one harvest is allowed to collect.</summary>
        public const int DefaultTriangleBudget = 250000;

        /// <summary>
        /// Geometry items whose extents reach within <paramref name="radius"/>
        /// of a point, nearest first.
        ///
        /// Descends from the model roots and skips any subtree whose own box is
        /// already out of reach. That prune is what makes this usable on a real
        /// federated model: a box that does not contain the point cannot have a
        /// child that does.
        /// </summary>
        public static ModelItemCollection ItemsNear(
            Document document,
            Point3D point,
            double radius,
            int maxItems)
        {
            var found = new ModelItemCollection();
            if (document == null) return found;
            if (maxItems <= 0) maxItems = DefaultItemBudget;

            var ranked = new List<KeyValuePair<double, ModelItem>>();
            try
            {
                foreach (ModelItem root in document.Models.RootItems)
                    Collect(root, point, radius, ranked, maxItems * 8);
            }
            catch (Exception)
            {
                // A model in the middle of a load, or an item whose box cannot
                // be read. Whatever was collected before that is still a valid
                // -- if smaller -- neighbourhood to snap against.
            }

            ranked.Sort(delegate (KeyValuePair<double, ModelItem> a, KeyValuePair<double, ModelItem> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            for (int i = 0; i < ranked.Count && found.Count < maxItems; i++)
                found.Add(ranked[i].Value);

            return found;
        }

        private static void Collect(
            ModelItem item,
            Point3D point,
            double radius,
            List<KeyValuePair<double, ModelItem>> into,
            int ceiling)
        {
            if (into.Count >= ceiling) return;

            double distance;
            if (!Reaches(item, point, radius, out distance)) return;

            if (item.HasGeometry)
            {
                into.Add(new KeyValuePair<double, ModelItem>(distance, item));
                // A geometry item's children, if any, are its own fragments as
                // far as the COM bridge is concerned; descending would harvest
                // them twice.
                return;
            }

            foreach (ModelItem child in item.Children)
                Collect(child, point, radius, into, ceiling);
        }

        /// <summary>
        /// Distance from a point to an item's bounding box, and whether that is
        /// inside the radius. Zero when the point is inside the box.
        /// </summary>
        private static bool Reaches(ModelItem item, Point3D point, double radius, out double distance)
        {
            distance = double.MaxValue;
            try
            {
                BoundingBox3D box = item.BoundingBox();
                if (box.IsEmpty) return false;

                double dx = Math.Max(0, Math.Max(box.Min.X - point.X, point.X - box.Max.X));
                double dy = Math.Max(0, Math.Max(box.Min.Y - point.Y, point.Y - box.Max.Y));
                double dz = Math.Max(0, Math.Max(box.Min.Z - point.Z, point.Z - box.Max.Z));
                distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return distance <= radius;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Tessellate a set of items into a soup, scaling every coordinate by
        /// <paramref name="scale"/> on the way out.
        ///
        /// The scale is applied here rather than by the caller so that a soup is
        /// always in one stated frame -- mixing document units and metres in the
        /// same list of triangles is the sort of thing that produces a snap
        /// 999 mm from where the user clicked and no error at all.
        /// </summary>
        public static MeshSoup Harvest(ModelItemCollection items, double scale, int triangleBudget)
        {
            var soup = new MeshSoup();
            if (items == null || items.Count == 0) return soup;
            if (triangleBudget <= 0) triangleBudget = DefaultTriangleBudget;

            try
            {
                InwOpSelection selection = ComApiBridge.ToInwOpSelection(items);
                foreach (object pathObject in selection.Paths())
                {
                    var path = pathObject as InwOaPath3;
                    if (path == null) continue;

                    foreach (object fragmentObject in path.Fragments())
                    {
                        var fragment = fragmentObject as InwOaFragment3;
                        if (fragment == null) continue;
                        if (soup.TriangleCount >= triangleBudget) return soup;

                        var sink = new PrimitiveSink(soup, scale, LocalToWorld(fragment), triangleBudget);
                        try
                        {
                            // eNONE: normals, colours and texture coordinates
                            // cost time to generate and nothing here reads them.
                            fragment.GenerateSimplePrimitives(nwEVertexProperty.eNONE, sink);
                        }
                        catch (Exception)
                        {
                            // One fragment that will not tessellate -- a point
                            // cloud region, a proxy, a reader that does not
                            // implement the callback. Keep the rest.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The COM bridge is unavailable, which happens when this runs
                // outside a live Navisworks. Whatever was gathered stands.
            }

            return soup;
        }

        /// <summary>
        /// The fragment's local-to-world matrix as 16 doubles, column-major, or
        /// null when it is the identity.
        ///
        /// Instanced geometry -- every bolt in a connection, every chair in a
        /// floor -- shares one tessellation and differs only by this matrix.
        /// Skipping it puts every instance on top of the first one.
        /// </summary>
        private static double[] LocalToWorld(InwOaFragment3 fragment)
        {
            try
            {
                var transform = fragment.GetLocalToWorldMatrix() as InwLTransform3f3;
                if (transform == null || transform.IsIdentity) return null;

                var values = transform.Matrix as Array;
                if (values == null || values.Length < 16) return null;

                var matrix = new double[16];
                int lower = values.GetLowerBound(0);
                for (int i = 0; i < 16; i++)
                    matrix[i] = Convert.ToDouble(values.GetValue(lower + i));
                return matrix;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The callback Navisworks drives, one primitive at a time.
        ///
        /// Implemented as a class rather than a lambda because the COM interface
        /// wants four methods, and kept private because nothing outside should
        /// hold one: it is stateful and single-use per fragment.
        /// </summary>
        private sealed class PrimitiveSink : InwSimplePrimitivesCB
        {
            private readonly MeshSoup _soup;
            private readonly double _scale;
            private readonly double[] _matrix;
            private readonly int _triangleBudget;

            public PrimitiveSink(MeshSoup soup, double scale, double[] matrix, int triangleBudget)
            {
                _soup = soup;
                _scale = scale == 0 ? 1 : scale;
                _matrix = matrix;
                _triangleBudget = triangleBudget;
            }

            public void Triangle(InwSimpleVertex v1, InwSimpleVertex v2, InwSimpleVertex v3)
            {
                if (_soup.TriangleCount >= _triangleBudget) return;
                _soup.AddTriangle(At(v1), At(v2), At(v3));
            }

            public void Line(InwSimpleVertex v1, InwSimpleVertex v2)
            {
                _soup.AddSegment(At(v1), At(v2));
            }

            public void Point(InwSimpleVertex v1)
            {
                // A bare point primitive is a real position in the model -- a
                // survey mark in a point-cloud region, a node in a line drawing
                // -- so it snaps like a corner.
                _soup.AddSnapPoint(At(v1));
            }

            public void SnapPoint(InwSimpleVertex v1)
            {
                _soup.AddSnapPoint(At(v1));
            }

            private Vec3 At(InwSimpleVertex vertex)
            {
                if (vertex == null) return new Vec3();

                double x = 0, y = 0, z = 0;
                try
                {
                    // coord is a VARIANT holding a float[3]; there is no typed
                    // accessor and there never has been.
                    var coordinates = vertex.coord as Array;
                    if (coordinates != null && coordinates.Length >= 3)
                    {
                        int lower = coordinates.GetLowerBound(0);
                        x = Convert.ToDouble(coordinates.GetValue(lower));
                        y = Convert.ToDouble(coordinates.GetValue(lower + 1));
                        z = Convert.ToDouble(coordinates.GetValue(lower + 2));
                    }
                }
                catch (Exception)
                {
                    return new Vec3();
                }

                if (_matrix != null)
                {
                    double wx = _matrix[0] * x + _matrix[4] * y + _matrix[8] * z + _matrix[12];
                    double wy = _matrix[1] * x + _matrix[5] * y + _matrix[9] * z + _matrix[13];
                    double wz = _matrix[2] * x + _matrix[6] * y + _matrix[10] * z + _matrix[14];
                    x = wx; y = wy; z = wz;
                }

                return new Vec3(x * _scale, y * _scale, z * _scale);
            }
        }
    }
}
