using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Nwc
{
    /// <summary>
    /// Reads a Wavefront OBJ into the shape <see cref="NwcMesh"/> wants.
    ///
    /// ## Why the plugin reads a mesh at all
    ///
    /// Until now it never did: geometry arrived as FBX and was handed straight
    /// to Navisworks, because the managed API cannot author geometry and this
    /// code cannot parse FBX. Writing NWC changes that -- nwcreate needs the
    /// triangles, so something here has to read them, and OBJ is the format
    /// worth reading. It is text, it is a few hundred lines to parse, the
    /// phone's exporter already writes it with the photographic atlas beside
    /// it, and it carries a texture coordinate per *polygon corner*, which is
    /// exactly what an atlas needs and what the NWC geometry stream takes.
    ///
    /// ## Indices
    ///
    /// OBJ indexes positions, texture coordinates and normals separately, so
    /// one corner is a triple. Vertices are keyed on that whole triple rather
    /// than on the position alone: two corners at the same point with different
    /// texture coordinates are different vertices, and merging them is how an
    /// atlas-textured mesh comes out smeared. They are one-based, and negative
    /// indices count back from the end -- both in the spec, both easy to get
    /// wrong in a way that only shows on somebody else's file.
    ///
    /// Faces with more than three corners are fanned. Ours only writes
    /// triangles; other people's do not.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class ObjReader
    {
        public sealed class Result
        {
            public NwcMesh Mesh;
            public string Message = "";
            public bool Ok { get { return Mesh != null; } }
            /// <summary>The `mtllib` this OBJ named, if any. Not resolved here
            /// -- the caller knows which folder it came from.</summary>
            public string MaterialLibrary = "";
        }

        public static Result ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Result { Message = "There is no OBJ at " + (path ?? "(no path)") + "." };
            Result result;
            try
            {
                using (var reader = new StreamReader(path))
                    result = Read(reader, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                return new Result { Message = "Could not read " + path + ": " + ex.Message };
            }

            // The atlas is found the way the format says to find it, through
            // the material library, rather than by guessing at a filename
            // beside the OBJ. Ours writes `<stem>.png`; somebody else's will
            // not, and the MTL is the only thing that actually knows.
            if (result.Ok)
                result.Mesh.TexturePath = TextureBeside(path, result.MaterialLibrary);
            return result;
        }

        /// <summary>
        /// The diffuse map named by <paramref name="materialLibrary"/>, as a
        /// full path, or "" when there is not one that exists.
        ///
        /// Only `map_Kd` is read. The others -- ambient, specular, bump -- have
        /// nowhere to go: the mesh carries one set of coordinates and the
        /// material gets one connected texture.
        /// </summary>
        internal static string TextureBeside(string objPath, string materialLibrary)
        {
            if (string.IsNullOrWhiteSpace(materialLibrary)) return "";
            string folder = Path.GetDirectoryName(Path.GetFullPath(objPath));
            if (string.IsNullOrEmpty(folder)) return "";

            string mtl = Path.Combine(folder, materialLibrary);
            if (!File.Exists(mtl)) return "";

            try
            {
                foreach (string line in File.ReadAllLines(mtl))
                {
                    string[] parts = line.Split(
                        new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!string.Equals(parts[0], "map_Kd", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // map_Kd takes options before the filename (-s, -o, -bm and
                    // friends, each with values). The filename is what is left
                    // once those are stepped over, and it may hold spaces, so
                    // the tail is rejoined rather than taken as one token.
                    int i = 1;
                    while (i < parts.Length && parts[i].StartsWith("-"))
                    {
                        i++;
                        while (i < parts.Length && !parts[i].StartsWith("-")
                               && IsNumber(parts[i])) i++;
                    }
                    if (i >= parts.Length) continue;

                    string name = string.Join(" ", parts, i, parts.Length - i);
                    string full = Path.IsPathRooted(name) ? name : Path.Combine(folder, name);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                }
            }
            catch (Exception)
            {
                // A material library we cannot read is a mesh without a
                // texture, not a mesh we refuse. The geometry is the point.
            }
            return "";
        }

        private static bool IsNumber(string text)
        {
            double ignored;
            return double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out ignored);
        }

        public static Result Read(TextReader reader, string name)
        {
            var result = new Result();
            if (reader == null) { result.Message = "There is nothing to read."; return result; }

            var positions = new List<Vec3>();
            var colors = new List<Vec3>();
            var normals = new List<Vec3>();
            var uvs = new List<double>();          // pairs
            bool anyVertexColour = false;

            var vertices = new List<Vec3>();
            var vertexNormals = new List<Vec3>();
            var vertexColors = new List<Vec3>();
            var triangles = new List<int>();
            var cornerUvs = new List<double>();     // pairs, one per emitted corner
            var seen = new Dictionary<string, int>();

            string line;
            long number = 0;
            while ((line = reader.ReadLine()) != null)
            {
                number++;
                if (line.Length == 0) continue;
                char first = line[0];
                if (first == '#') continue;

                string[] parts = line.Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                switch (parts[0])
                {
                    case "v":
                    {
                        if (parts.Length < 4)
                            return Fail(result, "line " + number + " is a vertex with "
                                              + (parts.Length - 1) + " numbers.");
                        positions.Add(new Vec3(
                            Number(parts[1]), Number(parts[2]), Number(parts[3])));
                        // The vertex-colour extension: three more numbers, 0..1.
                        // Not in the original spec, written by this suite and
                        // read by enough tools to be worth keeping.
                        if (parts.Length >= 7)
                        {
                            colors.Add(new Vec3(
                                Number(parts[4]), Number(parts[5]), Number(parts[6])));
                            anyVertexColour = true;
                        }
                        else
                        {
                            colors.Add(new Vec3(1, 1, 1));
                        }
                        break;
                    }
                    case "vn":
                        if (parts.Length < 4)
                            return Fail(result, "line " + number + " is a normal with "
                                              + (parts.Length - 1) + " numbers.");
                        normals.Add(new Vec3(
                            Number(parts[1]), Number(parts[2]), Number(parts[3])));
                        break;
                    case "vt":
                        if (parts.Length < 3)
                            return Fail(result, "line " + number + " is a texture coordinate "
                                              + "with " + (parts.Length - 1) + " numbers.");
                        uvs.Add(Number(parts[1]));
                        uvs.Add(Number(parts[2]));
                        break;
                    case "mtllib":
                        if (parts.Length >= 2) result.MaterialLibrary = parts[1];
                        break;
                    case "f":
                    {
                        if (parts.Length < 4)
                            return Fail(result, "line " + number + " is a face with "
                                              + (parts.Length - 1) + " corners.");

                        // Fan from the first corner. For a triangle this is the
                        // triangle; for a quad it is two triangles.
                        var fan = new List<int>(parts.Length - 1);
                        for (int i = 1; i < parts.Length; i++)
                        {
                            int index = Corner(
                                parts[i], positions, normals, uvs, colors, anyVertexColour,
                                vertices, vertexNormals, vertexColors, cornerUvs, seen,
                                number, result);
                            if (index < 0) return result;
                            fan.Add(index);
                        }
                        for (int i = 1; i + 1 < fan.Count; i++)
                        {
                            triangles.Add(fan[0]);
                            triangles.Add(fan[i]);
                            triangles.Add(fan[i + 1]);
                        }
                        break;
                    }
                }
            }

            if (positions.Count == 0) return Fail(result, "it has no vertices.");
            if (triangles.Count == 0) return Fail(result, "it has no faces.");

            // The corner UV list was built one entry per *emitted vertex*, not
            // per triangle corner, because a vertex is only emitted once per
            // distinct triple. Rebuild it against the triangles so the mesh
            // holds one UV per corner, which is what the geometry stream wants.
            var perCorner = new List<double>(triangles.Count * 2);
            bool haveUvs = cornerUvs.Count == vertices.Count * 2;
            if (haveUvs)
            {
                foreach (int v in triangles)
                {
                    perCorner.Add(cornerUvs[v * 2]);
                    perCorner.Add(cornerUvs[v * 2 + 1]);
                }
            }

            var mesh = new NwcMesh
            {
                Name = string.IsNullOrWhiteSpace(name) ? "PIXMYD scan" : name,
                Vertices = vertices,
                Triangles = triangles,
                Units = NwcApi.LinearUnits.Meters,
            };
            if (vertexNormals.Count == vertices.Count) mesh.Normals = vertexNormals;
            if (anyVertexColour && vertexColors.Count == vertices.Count) mesh.Colors = vertexColors;
            if (haveUvs) mesh.Uvs = perCorner;

            string wrong = mesh.Problem();
            if (wrong != null) return Fail(result, wrong);

            result.Mesh = mesh;
            result.Message = vertices.Count + " vertices, " + mesh.TriangleCount + " triangles.";
            return result;
        }

        /// <summary>
        /// One `v/vt/vn` corner, deduplicated on the whole triple.
        /// Returns the vertex index, or -1 with the reason on the result.
        /// </summary>
        private static int Corner(
            string token,
            List<Vec3> positions, List<Vec3> normals, List<double> uvs, List<Vec3> colors,
            bool anyVertexColour,
            List<Vec3> vertices, List<Vec3> vertexNormals, List<Vec3> vertexColors,
            List<double> cornerUvs, Dictionary<string, int> seen,
            long line, Result result)
        {
            int existing;
            if (seen.TryGetValue(token, out existing)) return existing;

            string[] bits = token.Split('/');
            int v = Index(bits.Length > 0 ? bits[0] : "", positions.Count);
            int t = bits.Length > 1 ? Index(bits[1], uvs.Count / 2) : -1;
            int n = bits.Length > 2 ? Index(bits[2], normals.Count) : -1;

            if (v < 0)
            {
                Fail(result, "line " + line + " refers to vertex \"" + token
                           + "\", which is not one of the " + positions.Count + " it has.");
                return -1;
            }

            vertices.Add(positions[v]);
            vertexColors.Add(v < colors.Count ? colors[v] : new Vec3(1, 1, 1));
            if (n >= 0) vertexNormals.Add(normals[n]);
            if (t >= 0)
            {
                // OBJ's V axis points up from the bottom-left; every consumer
                // here works top-down, so it is flipped once, on the way in.
                cornerUvs.Add(uvs[t * 2]);
                cornerUvs.Add(1.0 - uvs[t * 2 + 1]);
            }

            int index = vertices.Count - 1;
            seen[token] = index;
            return index;
        }

        /// <summary>
        /// A one-based OBJ index, or negative counting back from the end, as a
        /// zero-based one. -1 when it is out of range or unparseable.
        /// </summary>
        private static int Index(string text, int count)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return -1;
            if (value > 0) value -= 1;
            else if (value < 0) value = count + value;
            else return -1;                       // zero is not a valid OBJ index
            return value >= 0 && value < count ? value : -1;
        }

        private static double Number(string text)
        {
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : 0.0;
        }

        private static Result Fail(Result result, string why)
        {
            result.Mesh = null;
            result.Message = "This OBJ cannot be read: " + why;
            return result;
        }
    }
}
