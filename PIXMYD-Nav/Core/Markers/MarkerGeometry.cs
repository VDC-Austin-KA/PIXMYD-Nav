using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Markers
{
    /// <summary>Which shape stands in for a point in the model.</summary>
    public enum MarkerShape
    {
        /// <summary>A faceted ball centred on the point. Reads at any angle and
        /// from any distance, which is what a coordinator scrolling a model
        /// actually needs.</summary>
        Sphere = 0,
        /// <summary>Three crossed bars. Cheaper, and the centre is visible from
        /// inside the shape, which a sphere's is not.</summary>
        Cross = 1
    }

    /// <summary>
    /// The triangles that make a point visible.
    ///
    /// A placed point has to be two things at once: something a person can see
    /// in the Navisworks viewport, and something that survives an export as
    /// real geometry rather than as an annotation. A viewpoint redline is
    /// neither -- it lives in the NWF and leaves nothing behind.
    ///
    /// So a point becomes a small solid, generated here and written as an
    /// appendable file by <see cref="MarkerDxf"/>. Three inches across by
    /// default: big enough to find in a model of a building, small enough that
    /// it does not swallow the corner it marks.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class MarkerGlyphs
    {
        /// <summary>Three inches, in metres. The default marker diameter.</summary>
        public const double DefaultDiameterMetres = 0.0762;

        /// <summary>
        /// Triangles for one marker, centred on <paramref name="centre"/>, as
        /// flat vertex triples (a, b, c, a, b, c, ...).
        /// </summary>
        public static List<Vec3> Build(MarkerShape shape, Vec3 centre, double diameter)
        {
            double radius = Math.Abs(diameter) * 0.5;
            if (radius <= 0) radius = DefaultDiameterMetres * 0.5;
            return shape == MarkerShape.Cross
                ? Cross(centre, radius)
                : Sphere(centre, radius);
        }

        /// <summary>
        /// An octahedron subdivided once and pushed out onto the sphere: 32
        /// triangles.
        ///
        /// Deliberately coarse. This is a marker, not a model of a ball, and a
        /// hundred points at 320 triangles each is a file that makes the whole
        /// model slower to navigate for no gain at any zoom a person uses.
        /// </summary>
        private static List<Vec3> Sphere(Vec3 centre, double radius)
        {
            var unit = new[]
            {
                new Vec3(1, 0, 0), new Vec3(-1, 0, 0),
                new Vec3(0, 1, 0), new Vec3(0, -1, 0),
                new Vec3(0, 0, 1), new Vec3(0, 0, -1)
            };
            // The eight octahedron faces, wound outward.
            var faces = new[]
            {
                new[] { 0, 2, 4 }, new[] { 2, 1, 4 }, new[] { 1, 3, 4 }, new[] { 3, 0, 4 },
                new[] { 2, 0, 5 }, new[] { 1, 2, 5 }, new[] { 3, 1, 5 }, new[] { 0, 3, 5 }
            };

            var triangles = new List<Vec3>(32 * 3);
            foreach (int[] face in faces)
            {
                Vec3 a = unit[face[0]], b = unit[face[1]], c = unit[face[2]];
                Vec3 ab = OntoSphere(Midpoint(a, b));
                Vec3 bc = OntoSphere(Midpoint(b, c));
                Vec3 ca = OntoSphere(Midpoint(c, a));
                Emit(triangles, centre, radius, a, ab, ca);
                Emit(triangles, centre, radius, ab, b, bc);
                Emit(triangles, centre, radius, ca, bc, c);
                Emit(triangles, centre, radius, ab, bc, ca);
            }
            return triangles;
        }

        /// <summary>
        /// Three square-section bars through the centre, one per axis: 24
        /// triangles. The bar is a tenth of its length across, which is thin
        /// enough to read as a cross and thick enough not to disappear when the
        /// viewport is looking along one of the arms.
        /// </summary>
        private static List<Vec3> Cross(Vec3 centre, double radius)
        {
            double half = radius * 0.1;
            var triangles = new List<Vec3>(24 * 3);
            for (int axis = 0; axis < 3; axis++)
                AddBar(triangles, centre, axis, radius, half);
            return triangles;
        }

        private static void AddBar(List<Vec3> triangles, Vec3 centre, int axis, double length, double half)
        {
            // The two axes the bar's cross-section lives in.
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            for (int side = 0; side < 4; side++)
            {
                // Walk the square cross-section: (+u, +v, -u, -v).
                double[] from = Corner(side, half);
                double[] to = Corner((side + 1) % 4, half);

                Vec3 a = Offset(centre, axis, -length, u, from[0], v, from[1]);
                Vec3 b = Offset(centre, axis, -length, u, to[0], v, to[1]);
                Vec3 c = Offset(centre, axis, length, u, to[0], v, to[1]);
                Vec3 d = Offset(centre, axis, length, u, from[0], v, from[1]);

                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(a); triangles.Add(c); triangles.Add(d);
            }
        }

        private static double[] Corner(int index, double half)
        {
            switch (index)
            {
                case 0: return new double[] { half, half };
                case 1: return new double[] { -half, half };
                case 2: return new double[] { -half, -half };
                default: return new double[] { half, -half };
            }
        }

        private static Vec3 Offset(Vec3 centre, int axis, double along, int u, double du, int v, double dv)
        {
            var xyz = new double[] { centre.X, centre.Y, centre.Z };
            xyz[axis] += along;
            xyz[u] += du;
            xyz[v] += dv;
            return new Vec3(xyz[0], xyz[1], xyz[2]);
        }

        private static void Emit(List<Vec3> into, Vec3 centre, double radius, Vec3 a, Vec3 b, Vec3 c)
        {
            into.Add(Place(centre, radius, a));
            into.Add(Place(centre, radius, b));
            into.Add(Place(centre, radius, c));
        }

        private static Vec3 Place(Vec3 centre, double radius, Vec3 unit)
        {
            return new Vec3(
                centre.X + unit.X * radius,
                centre.Y + unit.Y * radius,
                centre.Z + unit.Z * radius);
        }

        private static Vec3 Midpoint(Vec3 a, Vec3 b)
        {
            return new Vec3((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
        }

        private static Vec3 OntoSphere(Vec3 v)
        {
            double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (length < 1e-15) return new Vec3(0, 0, 1);
            return new Vec3(v.X / length, v.Y / length, v.Z / length);
        }
    }

    /// <summary>
    /// The markers as a file Navisworks can append.
    ///
    /// ## Why DXF and not FBX
    ///
    /// The managed Navisworks API cannot author geometry into an open document
    /// -- there is no AddGeometry to call, and this repository has said so
    /// honestly rather than pretending otherwise. What it can do is
    /// <c>Document.AppendFile</c>. So a marker becomes a file, and the question
    /// is which format.
    ///
    /// DXF, for reasons that are all about the person using it:
    ///
    /// * **Layers become names in the selection tree.** Each point gets its own
    ///   layer, so P007 in the point list is findable as P007 in Navisworks --
    ///   selectable, isolatable, and reportable. An STL is one anonymous blob
    ///   and a hand-written FBX would need a node graph to say the same thing.
    /// * **It is ASCII.** A file this plugin generates and cannot open in
    ///   Navisworks on a Linux CI box is a file the tests can still read back
    ///   line by line, which is the only independent check available here.
    /// * **A DXF POINT is literally a 3D point.** The contract asks for a point,
    ///   and the format has one; the sphere beside it is what makes it visible.
    ///
    /// R12 (AC1009) on purpose: it is the most widely readable DXF there is,
    /// and nothing here needs anything newer than 3DFACE.
    ///
    /// Coordinates are written in whatever units the caller passes, which must
    /// be the document's own -- an appended file lands at its own coordinates,
    /// so a marker written in metres into a model drawn in millimetres arrives
    /// a thousand times too close to the origin.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class MarkerDxf
    {
        public const string FileName = "points-markers.dxf";
        public const string LayerPrefix = "PIXMYD_";
        /// <summary>AutoCAD colour index 1. Red reads against grey structure and
        /// is not a colour BIM models usually use for anything real.</summary>
        public const int ColourIndex = 1;

        /// <summary>
        /// Render the whole marker set.
        ///
        /// <paramref name="positions"/> and <paramref name="diameter"/> are in
        /// the document's units. Ids name the layers.
        /// </summary>
        public static string Render(
            IList<string> ids,
            IList<Vec3> positions,
            MarkerShape shape,
            double diameter)
        {
            if (ids == null || positions == null || ids.Count != positions.Count)
                throw new ArgumentException("Every marker needs an id and a position.");

            var sb = new StringBuilder();
            var layers = new List<string>(ids.Count);
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids) layers.Add(UniqueLayerName(id, used));

            WriteHeader(sb, positions, diameter);
            WriteTables(sb, layers);

            Section(sb, "ENTITIES");
            for (int i = 0; i < positions.Count; i++)
            {
                // The point itself first: it is the deliverable, and the solid
                // around it is the aid. A consumer that reads only POINT
                // entities gets exactly the coordinates and nothing else.
                Pair(sb, 0, "POINT");
                Pair(sb, 8, layers[i]);
                Vertex(sb, 10, positions[i]);

                foreach (Vec3[] face in Faces(shape, positions[i], diameter))
                {
                    Pair(sb, 0, "3DFACE");
                    Pair(sb, 8, layers[i]);
                    Vertex(sb, 10, face[0]);
                    Vertex(sb, 11, face[1]);
                    Vertex(sb, 12, face[2]);
                    // R12 3DFACE is always four-cornered; a triangle repeats its
                    // last corner, which is the documented way to say "three".
                    Vertex(sb, 13, face[2]);
                }
            }
            EndSection(sb);

            Pair(sb, 0, "EOF");
            return sb.ToString();
        }

        public static void Write(
            string path,
            IList<string> ids,
            IList<Vec3> positions,
            MarkerShape shape,
            double diameter)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // ASCII, because a DXF group code line has no encoding declaration
            // and a BOM in front of the first "  0" is how a reader decides the
            // file is not a DXF.
            File.WriteAllText(path, Render(ids, positions, shape, diameter), new UTF8Encoding(false));
        }

        private static IEnumerable<Vec3[]> Faces(MarkerShape shape, Vec3 centre, double diameter)
        {
            List<Vec3> triangles = MarkerGlyphs.Build(shape, centre, diameter);
            for (int t = 0; t + 2 < triangles.Count; t += 3)
                yield return new[] { triangles[t], triangles[t + 1], triangles[t + 2] };
        }

        private static void WriteHeader(StringBuilder sb, IList<Vec3> positions, double diameter)
        {
            double radius = Math.Abs(diameter) * 0.5;
            Vec3 min = new Vec3(0, 0, 0), max = new Vec3(0, 0, 0);
            if (positions.Count > 0)
            {
                min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
                max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
                foreach (Vec3 p in positions)
                {
                    min = new Vec3(Math.Min(min.X, p.X - radius), Math.Min(min.Y, p.Y - radius), Math.Min(min.Z, p.Z - radius));
                    max = new Vec3(Math.Max(max.X, p.X + radius), Math.Max(max.Y, p.Y + radius), Math.Max(max.Z, p.Z + radius));
                }
            }

            Section(sb, "HEADER");
            Pair(sb, 9, "$ACADVER"); Pair(sb, 1, "AC1009");
            Pair(sb, 9, "$INSBASE"); Vertex(sb, 10, new Vec3(0, 0, 0));
            Pair(sb, 9, "$EXTMIN"); Vertex(sb, 10, min);
            Pair(sb, 9, "$EXTMAX"); Vertex(sb, 10, max);
            EndSection(sb);
        }

        private static void WriteTables(StringBuilder sb, IList<string> layers)
        {
            var distinct = new List<string>();
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string layer in layers)
            {
                if (seen.ContainsKey(layer)) continue;
                seen[layer] = true;
                distinct.Add(layer);
            }

            Section(sb, "TABLES");
            Pair(sb, 0, "TABLE");
            Pair(sb, 2, "LAYER");
            Pair(sb, 70, distinct.Count);
            foreach (string layer in distinct)
            {
                Pair(sb, 0, "LAYER");
                Pair(sb, 2, layer);
                Pair(sb, 70, 0);
                Pair(sb, 62, ColourIndex);
                Pair(sb, 6, "CONTINUOUS");
            }
            Pair(sb, 0, "ENDTAB");
            EndSection(sb);
        }

        /// <summary>
        /// A layer name R12 accepts, derived from a point id.
        ///
        /// R12 layer names are at most 31 characters and cannot contain
        /// whitespace or any of &lt; &gt; / \ " : ; ? * | = '. A point id is a
        /// user-editable string, so it is filtered rather than trusted --
        /// a stray slash in a label would otherwise produce a file AutoCAD and
        /// Navisworks both refuse, long after the point was placed.
        /// </summary>
        public static string LayerNameFor(string id)
        {
            var kept = new StringBuilder(31);
            foreach (char c in (id ?? "").ToUpperInvariant())
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                          c == '-' || c == '_' || c == '$';
                kept.Append(ok ? c : '_');
                if (kept.Length + LayerPrefix.Length >= 31) break;
            }
            string body = kept.ToString();
            return LayerPrefix + (body.Length == 0 ? "POINT" : body);
        }

        private static string UniqueLayerName(string id, Dictionary<string, int> used)
        {
            string name = LayerNameFor(id);
            int count;
            if (!used.TryGetValue(name, out count))
            {
                used[name] = 1;
                return name;
            }
            // Two ids that differ only in a filtered character would otherwise
            // share a layer and become indistinguishable in the tree.
            used[name] = count + 1;
            string suffixed = name + "_" + (count + 1).ToString(CultureInfo.InvariantCulture);
            return suffixed.Length <= 31 ? suffixed : suffixed.Substring(0, 31);
        }

        // MARK: - Group code pairs

        private static void Section(StringBuilder sb, string name)
        {
            Pair(sb, 0, "SECTION");
            Pair(sb, 2, name);
        }

        private static void EndSection(StringBuilder sb) { Pair(sb, 0, "ENDSEC"); }

        private static void Vertex(StringBuilder sb, int baseCode, Vec3 p)
        {
            Pair(sb, baseCode, p.X);
            Pair(sb, baseCode + 10, p.Y);
            Pair(sb, baseCode + 20, p.Z);
        }

        private static void Pair(StringBuilder sb, int code, string value)
        {
            sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(value).Append("\r\n");
        }

        private static void Pair(StringBuilder sb, int code, int value)
        {
            Pair(sb, code, value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Pair(StringBuilder sb, int code, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            // Fixed six decimals: a DXF real has no exponent form, and "1E-05"
            // is how a coordinate becomes unreadable to half the parsers that
            // exist. Six places is a micrometre in metres and a nanometre in
            // millimetres -- past any model tolerance either way.
            Pair(sb, code, value.ToString("0.000000", CultureInfo.InvariantCulture));
        }
    }
}
