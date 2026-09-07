using System;
using System.Collections.Generic;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Nwc
{
    /// <summary>
    /// A triangle mesh on its way into an NWC, and the arithmetic of getting
    /// it there.
    ///
    /// Split from <see cref="NwcWriter"/> deliberately. The writer cannot be
    /// exercised without the nwcreate runtime, so everything that decides
    /// *what* is handed to that runtime lives here, where it can be tested
    /// offline -- index bounds, the per-corner expansion the geometry stream
    /// wants, and which vertex properties the mesh actually carries.
    ///
    /// That division is not tidiness. A bad index handed to a native API is an
    /// access violation inside Navisworks, and the useful place to catch it is
    /// a test, not a crash report.
    /// </summary>
    public sealed class NwcMesh
    {
        /// <summary>Vertex positions, in the units named by <see cref="Units"/>.</summary>
        public IList<Vec3> Vertices = new List<Vec3>();
        /// <summary>Index triples into <see cref="Vertices"/>.</summary>
        public IList<int> Triangles = new List<int>();
        /// <summary>One per vertex, or empty.</summary>
        public IList<Vec3> Normals = new List<Vec3>();
        /// <summary>One RGB triple in 0..1 per vertex, or empty.</summary>
        public IList<Vec3> Colors = new List<Vec3>();
        /// <summary>One UV pair per *polygon corner*, or empty.</summary>
        public IList<double> Uvs = new List<double>();

        /// <summary>What the coordinates are in. Metres, for anything this
        /// suite produces: the capture contract fixes metres and the scene
        /// declares the same, so nothing has to guess a scale.</summary>
        public NwcApi.LinearUnits Units = NwcApi.LinearUnits.Meters;

        public string Name = "PIXMYD scan";

        public int TriangleCount { get { return Triangles.Count / 3; } }
        public bool HasNormals { get { return Normals.Count == Vertices.Count && Vertices.Count > 0; } }
        public bool HasColors { get { return Colors.Count == Vertices.Count && Vertices.Count > 0; } }
        public bool HasUvs { get { return Uvs.Count == Triangles.Count * 2 && Triangles.Count > 0; } }

        /// <summary>
        /// The vertex properties this mesh can actually supply.
        ///
        /// Declared per mesh rather than per stream call, because the stream is
        /// begun with a bitfield and every vertex afterwards must carry exactly
        /// those properties. Promising colours and then not writing one for
        /// some vertex is not an error the API reports; it is a mesh that comes
        /// out wrong.
        /// </summary>
        public NwcApi.VertexProperty Properties
        {
            get
            {
                NwcApi.VertexProperty p = NwcApi.VertexProperty.None;
                if (HasNormals) p |= NwcApi.VertexProperty.Normal;
                if (HasColors) p |= NwcApi.VertexProperty.Color;
                if (HasUvs) p |= NwcApi.VertexProperty.TexCoord;
                return p;
            }
        }

        /// <summary>One corner of one triangle, with everything it carries.</summary>
        public struct Corner
        {
            public Vec3 Position;
            public Vec3 Normal;
            public Vec3 Color;
            public double U;
            public double V;
        }

        /// <summary>
        /// Why a mesh cannot be written, or null when it can.
        ///
        /// Checked before a single native call, because the alternative is
        /// finding out inside nwcreate.
        /// </summary>
        public string Problem()
        {
            if (Vertices.Count == 0) return "This mesh has no vertices.";
            if (Triangles.Count == 0) return "This mesh has no triangles.";
            if (Triangles.Count % 3 != 0)
                return "This mesh has " + Triangles.Count + " indices, which is not a whole "
                     + "number of triangles.";
            for (int i = 0; i < Triangles.Count; i++)
            {
                int index = Triangles[i];
                if (index < 0 || index >= Vertices.Count)
                    return "Index " + index + " at position " + i + " is outside the "
                         + Vertices.Count + " vertices this mesh has.";
            }
            return null;
        }

        /// <summary>
        /// The corners, in the order the geometry stream wants them: three per
        /// triangle, each carrying its own copy of everything.
        ///
        /// The stream is not indexed -- `LiNwcGeometryStreamTriangleVertex`
        /// takes a position, not an index -- so a shared vertex is emitted once
        /// per triangle that uses it. That is the API's shape, not a choice
        /// here, and it is why UVs are stored per corner: a texture atlas gives
        /// every triangle its own tile, so the same vertex has a different
        /// coordinate in each triangle it belongs to, and there is nowhere to
        /// put that in a per-vertex array.
        /// </summary>
        public IEnumerable<Corner> Corners()
        {
            bool normals = HasNormals, colors = HasColors, uvs = HasUvs;
            for (int i = 0; i < Triangles.Count; i++)
            {
                int v = Triangles[i];
                var corner = new Corner { Position = Vertices[v] };
                if (normals) corner.Normal = Normals[v];
                if (colors) corner.Color = Colors[v];
                if (uvs)
                {
                    corner.U = Uvs[i * 2];
                    corner.V = Uvs[i * 2 + 1];
                }
                yield return corner;
            }
        }
    }
}
