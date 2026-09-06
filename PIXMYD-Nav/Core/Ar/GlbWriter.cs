using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Ar
{
    /// <summary>
    /// glTF 2.0 binary, for the one thing the AR export was missing: geometry.
    ///
    /// ar-model.json has shipped a bounding box, a camera and a photograph
    /// since the first version, and the phone's own decoder says so out loud --
    /// "the shipping plugin does not write one yet ... a bundle with no
    /// geometry is still worth showing. It just cannot be drawn over the
    /// world." That is the gap this closes. Navisworks' geometry is reachable
    /// through the COM primitive bridge (Core/NavBridge/PrimitiveHarvester.cs);
    /// what was missing was somewhere to put it.
    ///
    /// ## Why GLB here and FBX the other way
    ///
    /// The two directions have different consumers and the right answer is not
    /// the same one twice. A scan going into Navisworks is FBX because
    /// Navisworks reads FBX natively and appending a file is the only way a
    /// plugin can add geometry to an open document. A model going out to the
    /// phone is GLB because that is what the phone can draw without a converter
    /// -- SceneKit and RealityKit both load it, the contract already named
    /// `.glb`, and the phone's own exporters write it.
    ///
    /// ## Scope
    ///
    /// One mesh, one material, positions and indices, optional normals and
    /// vertex colours. No textures, no animation, no scene graph. An AR
    /// overlay is a shape you look through; it is not a rendering of the model,
    /// and every feature not written here is a feature that cannot be wrong.
    ///
    /// Y-up on the way out. glTF fixes +Y as up and Navisworks models are
    /// almost always Z-up, so the conversion happens here rather than being
    /// left for the phone to guess from a provenance string.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class GlbWriter
    {
        public const string FileName = "ar-model.glb";

        private const uint Magic = 0x46546C67;      // "glTF"
        private const uint Version = 2;
        private const uint JsonChunk = 0x4E4F534A;  // "JSON"
        private const uint BinaryChunk = 0x004E4942; // "BIN\0"

        private const int ComponentFloat = 5126;
        private const int ComponentUnsignedInt = 5125;
        private const int TargetArrayBuffer = 34962;
        private const int TargetElementArrayBuffer = 34963;

        public sealed class Options
        {
            public string Name = "model";
            /// <summary>Rotate Z-up source coordinates into glTF's Y-up.</summary>
            public bool ZUpToYUp = true;
            /// <summary>Subtracted from every vertex before writing, so the mesh
            /// sits near its own origin however far out the model is surveyed.</summary>
            public Vec3 Offset;
            public bool IncludeNormals = true;
        }

        /// <summary>
        /// Write the triangles of a soup as one GLB.
        ///
        /// Returns the byte count written. Throws only for I/O -- an empty soup
        /// produces a valid, empty-mesh file rather than an exception, because
        /// the caller has already decided the export is worth doing and a
        /// missing file is harder to explain than an empty one.
        /// </summary>
        public static long Write(string path, MeshSoup soup, Options options)
        {
            byte[] bytes = Render(soup, options);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, bytes);
            return bytes.Length;
        }

        public static byte[] Render(MeshSoup soup, Options options)
        {
            if (options == null) options = new Options();

            var positions = new List<float>();
            var normals = new List<float>();
            var colors = new List<float>();
            var indices = new List<uint>();
            Build(soup, options, positions, normals, colors, indices);

            int vertexCount = positions.Count / 3;
            bool hasNormals = options.IncludeNormals && normals.Count == positions.Count && vertexCount > 0;
            // Only when the soup actually carries item colours. A buffer of the
            // same grey repeated three times per triangle is half again the
            // file for nothing, on geometry that has to cross a phone's Wi-Fi.
            bool hasColors = soup != null && soup.HasColor
                && colors.Count == positions.Count && vertexCount > 0;

            var binary = new MemoryStream();
            var views = new List<int[]>();  // offset, length, target

            int positionView = AddView(binary, views, Floats(positions), TargetArrayBuffer);
            int normalView = hasNormals ? AddView(binary, views, Floats(normals), TargetArrayBuffer) : -1;
            int colorView = hasColors ? AddView(binary, views, Floats(colors), TargetArrayBuffer) : -1;
            int indexView = AddView(binary, views, UInts(indices), TargetElementArrayBuffer);

            float[] min, max;
            Extents(positions, out min, out max);

            string json = Json(
                options.Name,
                vertexCount,
                indices.Count,
                positionView, normalView, colorView, indexView,
                views,
                (int)binary.Length,
                min, max);

            return Pack(json, binary.ToArray());
        }

        private static void Build(
            MeshSoup soup,
            Options options,
            List<float> positions,
            List<float> normals,
            List<float> colors,
            List<uint> indices)
        {
            if (soup == null) return;

            // Flat-shaded: every triangle gets its own three vertices. A shared
            // vertex would need an averaged normal, and averaging across the
            // sharp edge between a wall and a slab is what makes a building
            // look like it was carved from soap.
            for (int t = 0; t + 2 < soup.Triangles.Count; t += 3)
            {
                Vec3 a = Place(soup.Vertices[soup.Triangles[t]], options);
                Vec3 b = Place(soup.Vertices[soup.Triangles[t + 1]], options);
                Vec3 c = Place(soup.Vertices[soup.Triangles[t + 2]], options);

                uint first = (uint)(positions.Count / 3);
                Append(positions, a);
                Append(positions, b);
                Append(positions, c);
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);

                // The colour is per vertex because glTF has nowhere else to put
                // it, but it is really per item: all three corners of a face
                // carry the colour the vertex they welded to was given.
                Append(colors, soup.Colors[soup.Triangles[t]]);
                Append(colors, soup.Colors[soup.Triangles[t + 1]]);
                Append(colors, soup.Colors[soup.Triangles[t + 2]]);

                if (!options.IncludeNormals) continue;
                Vec3 n = Normal(a, b, c);
                Append(normals, n);
                Append(normals, n);
                Append(normals, n);
            }
        }

        private static Vec3 Place(Vec3 v, Options options)
        {
            double x = v.X - options.Offset.X;
            double y = v.Y - options.Offset.Y;
            double z = v.Z - options.Offset.Z;
            // Z-up to Y-up is a -90 degree turn about X: (x, y, z) -> (x, z, -y).
            return options.ZUpToYUp ? new Vec3(x, z, -y) : new Vec3(x, y, z);
        }

        private static Vec3 Normal(Vec3 a, Vec3 b, Vec3 c)
        {
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-15) return new Vec3(0, 1, 0);
            return new Vec3(nx / length, ny / length, nz / length);
        }

        private static void Append(List<float> into, Vec3 v)
        {
            into.Add((float)v.X);
            into.Add((float)v.Y);
            into.Add((float)v.Z);
        }

        private static void Extents(List<float> positions, out float[] min, out float[] max)
        {
            min = new float[] { 0, 0, 0 };
            max = new float[] { 0, 0, 0 };
            if (positions.Count < 3) return;

            min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
            max = new float[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i + 2 < positions.Count; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    float value = positions[i + k];
                    if (value < min[k]) min[k] = value;
                    if (value > max[k]) max[k] = value;
                }
        }

        // MARK: - Buffer views

        private static int AddView(MemoryStream binary, List<int[]> views, byte[] data, int target)
        {
            // Every accessor's offset must be a multiple of its component size,
            // and the spec requires four-byte alignment for buffer views.
            while (binary.Length % 4 != 0) binary.WriteByte(0);
            int offset = (int)binary.Length;
            binary.Write(data, 0, data.Length);
            views.Add(new int[] { offset, data.Length, target });
            return views.Count - 1;
        }

        private static byte[] Floats(List<float> values)
        {
            var bytes = new byte[values.Count * 4];
            for (int i = 0; i < values.Count; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, bytes, i * 4, 4);
            return bytes;
        }

        private static byte[] UInts(List<uint> values)
        {
            var bytes = new byte[values.Count * 4];
            for (int i = 0; i < values.Count; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, bytes, i * 4, 4);
            return bytes;
        }

        // MARK: - JSON

        private static string Json(
            string name,
            int vertexCount,
            int indexCount,
            int positionView,
            int normalView,
            int colorView,
            int indexView,
            List<int[]> views,
            int bufferLength,
            float[] min,
            float[] max)
        {
            var sb = new StringBuilder();
            sb.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"PIXMYD-Nav\"}");
            sb.Append(",\"scene\":0,\"scenes\":[{\"nodes\":[0]}]");
            sb.Append(",\"nodes\":[{\"mesh\":0,\"name\":").Append(Quote(name)).Append("}]");

            // Accessors are emitted below in exactly this order, so the
            // numbering is computed once here rather than written as literals
            // that quietly go wrong the moment an attribute is added.
            int next = 0;
            int positionAccessor = next++;
            int normalAccessor = normalView >= 0 ? next++ : -1;
            int colorAccessor = colorView >= 0 ? next++ : -1;
            int indexAccessor = next++;

            sb.Append(",\"meshes\":[{\"name\":").Append(Quote(name))
              .Append(",\"primitives\":[{\"attributes\":{\"POSITION\":")
              .Append(positionAccessor.ToString(CultureInfo.InvariantCulture));
            if (normalAccessor >= 0)
                sb.Append(",\"NORMAL\":").Append(normalAccessor.ToString(CultureInfo.InvariantCulture));
            if (colorAccessor >= 0)
                sb.Append(",\"COLOR_0\":").Append(colorAccessor.ToString(CultureInfo.InvariantCulture));
            sb.Append("},\"indices\":").Append(indexAccessor.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"material\":0,\"mode\":4}]}]");

            // glTF multiplies COLOR_0 into the base colour factor, so carrying
            // item colours means the factor has to be white -- left at the old
            // blue-grey it would tint every item towards it and a red valve
            // would arrive mauve. With no colours the factor is the colour, and
            // stays exactly what every earlier AR model used.
            string baseColor = colorAccessor >= 0
                ? "[1.0,1.0,1.0,1.0]"
                : "[0.62,0.68,0.75,1.0]";
            sb.Append(",\"materials\":[{\"name\":\"PIXMYD\",\"pbrMetallicRoughness\":{")
              .Append("\"baseColorFactor\":").Append(baseColor)
              .Append(",\"metallicFactor\":0.0,\"roughnessFactor\":0.9}")
              .Append(",\"doubleSided\":true}]");

            sb.Append(",\"accessors\":[");
            Accessor(sb, positionView, ComponentFloat, vertexCount, "VEC3", min, max, true);
            if (normalView >= 0)
            {
                sb.Append(',');
                Accessor(sb, normalView, ComponentFloat, vertexCount, "VEC3", null, null, false);
            }
            if (colorView >= 0)
            {
                sb.Append(',');
                Accessor(sb, colorView, ComponentFloat, vertexCount, "VEC3", null, null, false);
            }
            sb.Append(',');
            Accessor(sb, indexView, ComponentUnsignedInt, indexCount, "SCALAR", null, null, false);
            sb.Append(']');

            sb.Append(",\"bufferViews\":[");
            for (int i = 0; i < views.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"buffer\":0,\"byteOffset\":").Append(views[i][0].ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"byteLength\":").Append(views[i][1].ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"target\":").Append(views[i][2].ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"buffers\":[{\"byteLength\":")
              .Append(bufferLength.ToString(CultureInfo.InvariantCulture)).Append("}]");
            sb.Append('}');
            return sb.ToString();
        }

        private static void Accessor(
            StringBuilder sb,
            int view,
            int componentType,
            int count,
            string type,
            float[] min,
            float[] max,
            bool withBounds)
        {
            sb.Append("{\"bufferView\":").Append(view.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"componentType\":").Append(componentType.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"type\":\"").Append(type).Append('"');
            if (withBounds && min != null && max != null)
            {
                // Required by the spec for POSITION, and used by every viewer to
                // frame the model before it has decoded a single triangle.
                sb.Append(",\"min\":").Append(Vector(min));
                sb.Append(",\"max\":").Append(Vector(max));
            }
            sb.Append('}');
        }

        private static string Vector(float[] v)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Number(v[i]));
            }
            return sb.Append(']').ToString();
        }

        private static string Number(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return "0";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // MARK: - Container

        private static byte[] Pack(string json, byte[] binary)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            // Chunks are padded to four bytes: JSON with spaces, BIN with zeros.
            // Not optional -- a loader that trusts the header reads past the end
            // of an unpadded file.
            int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
            int binaryPadding = (4 - (binary.Length % 4)) % 4;

            int total = 12 + 8 + jsonBytes.Length + jsonPadding + 8 + binary.Length + binaryPadding;
            var output = new MemoryStream(total);
            var writer = new BinaryWriter(output);

            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((uint)total);

            writer.Write((uint)(jsonBytes.Length + jsonPadding));
            writer.Write(JsonChunk);
            writer.Write(jsonBytes);
            for (int i = 0; i < jsonPadding; i++) writer.Write((byte)0x20);

            writer.Write((uint)(binary.Length + binaryPadding));
            writer.Write(BinaryChunk);
            writer.Write(binary);
            for (int i = 0; i < binaryPadding; i++) writer.Write((byte)0);

            writer.Flush();
            return output.ToArray();
        }
    }
}
