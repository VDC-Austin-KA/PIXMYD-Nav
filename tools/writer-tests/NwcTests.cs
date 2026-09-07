using System;
using System.Collections.Generic;
using PIXMYD_Nav.Core.Nwc;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav
{
    /// <summary>
    /// The half of NWC writing that can be checked without Navisworks.
    ///
    /// NwcWriter cannot run here -- it is native calls into a library that only
    /// exists inside the application. So everything deciding what those calls
    /// receive lives in NwcMesh, and this is where it is held to account: a bad
    /// index reaching nwcreate is an access violation in the user's session,
    /// and a test is a much better place to find one.
    /// </summary>
    internal static class NwcTests
    {
        public static int Run()
        {
            int failures = 0;
            RefusesAMeshItCannotWrite(ref failures);
            CornersRepeatSharedVerticesForAnUnindexedStream(ref failures);
            PropertiesAreOnlyPromisedWhenTheyCanBeDelivered(ref failures);
            UvsAreCarriedPerCornerNotPerVertex(ref failures);
            return failures;
        }

        private static NwcMesh Quad()
        {
            var m = new NwcMesh();
            m.Vertices = new List<Vec3> {
                new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0) };
            m.Triangles = new List<int> { 0, 1, 2, 1, 3, 2 };
            return m;
        }

        /// <summary>Every refusal here is one that would otherwise be a native crash.</summary>
        private static void RefusesAMeshItCannotWrite(ref int failures)
        {
            Program.Check(new NwcMesh().Problem() != null,
                "an empty mesh is refused", ref failures);

            var noFaces = new NwcMesh();
            noFaces.Vertices = new List<Vec3> { new Vec3(0, 0, 0) };
            Program.Check(noFaces.Problem() != null,
                "a mesh with no triangles is refused", ref failures);

            var ragged = Quad();
            ragged.Triangles = new List<int> { 0, 1 };
            Program.Check(ragged.Problem() != null,
                "an index count that is not a multiple of three is refused", ref failures);

            var past = Quad();
            past.Triangles = new List<int> { 0, 1, 9 };
            string why = past.Problem();
            Program.Check(why != null && why.Contains("9"),
                "an index past the end is refused, and says which, got " + (why ?? "null"),
                ref failures);

            var negative = Quad();
            negative.Triangles = new List<int> { 0, -1, 2 };
            Program.Check(negative.Problem() != null, "a negative index is refused", ref failures);

            Program.Check(Quad().Problem() == null, "a sound mesh is not refused", ref failures);
        }

        /// <summary>
        /// The geometry stream takes positions, not indices, so a vertex shared
        /// by two triangles is written twice. Getting this wrong produces a
        /// mesh with holes where the shared edges were.
        /// </summary>
        private static void CornersRepeatSharedVerticesForAnUnindexedStream(ref int failures)
        {
            var corners = new List<NwcMesh.Corner>(Quad().Corners());
            Program.Check(corners.Count == 6,
                "two triangles are six corners, got " + corners.Count, ref failures);

            int atOne = 0, atTwo = 0;
            foreach (NwcMesh.Corner c in corners)
            {
                if (c.Position.X == 1 && c.Position.Y == 0) atOne++;
                if (c.Position.X == 0 && c.Position.Y == 1) atTwo++;
            }
            Program.Check(atOne == 2 && atTwo == 2,
                "a shared vertex is emitted once per triangle that uses it, got "
                    + atOne + " and " + atTwo, ref failures);
        }

        /// <summary>
        /// The stream is begun with a bitfield, and every vertex afterwards has
        /// to carry exactly those properties. Promising colours and then not
        /// writing one is not an error the API reports -- it is a wrong model.
        /// </summary>
        private static void PropertiesAreOnlyPromisedWhenTheyCanBeDelivered(ref int failures)
        {
            NwcMesh bare = Quad();
            Program.Check(bare.Properties == NwcApi.VertexProperty.None,
                "a bare mesh promises nothing", ref failures);

            var everyColour = new List<Vec3> {
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 1, 0) };

            NwcMesh coloured = Quad();
            coloured.Colors = everyColour;
            Program.Check(coloured.Properties == NwcApi.VertexProperty.Color,
                "colours are promised when there is one per vertex", ref failures);

            // One short. Anything but "no colours" here leaves the stream
            // expecting a colour the loop cannot supply.
            NwcMesh short_ = Quad();
            short_.Colors = new List<Vec3> { new Vec3(1, 0, 0) };
            Program.Check(short_.Properties == NwcApi.VertexProperty.None,
                "a colour array that does not cover every vertex promises nothing", ref failures);

            NwcMesh full = Quad();
            full.Normals = new List<Vec3> {
                new Vec3(0, 0, 1), new Vec3(0, 0, 1), new Vec3(0, 0, 1), new Vec3(0, 0, 1) };
            full.Colors = everyColour;
            full.Uvs = new List<double>();
            for (int i = 0; i < 12; i++) full.Uvs.Add(0.5);
            Program.Check(
                full.Properties == (NwcApi.VertexProperty.Normal | NwcApi.VertexProperty.Color
                                    | NwcApi.VertexProperty.TexCoord),
                "all three are promised together when all three are present", ref failures);
        }

        /// <summary>
        /// A texture atlas gives every triangle its own tile, so one vertex has
        /// a different coordinate in each triangle it belongs to. Per-vertex
        /// UVs cannot express that, which is why they are stored per corner.
        /// </summary>
        private static void UvsAreCarriedPerCornerNotPerVertex(ref int failures)
        {
            NwcMesh m = Quad();
            m.Uvs = new List<double>();
            for (int i = 0; i < 6; i++) { m.Uvs.Add(i / 10.0); m.Uvs.Add(1 - i / 10.0); }

            Program.Check(m.HasUvs,
                "twelve numbers is two UVs per corner for six corners", ref failures);

            var corners = new List<NwcMesh.Corner>(m.Corners());
            for (int i = 0; i < corners.Count; i++)
            {
                Program.Check(Math.Abs(corners[i].U - i / 10.0) < 1e-12,
                    "corner " + i + " takes the UV at its own position in the list", ref failures);
            }

            // Vertex 1 appears in both triangles with different coordinates,
            // which is exactly what per-vertex storage could not hold.
            Program.Check(Math.Abs(corners[1].U - corners[3].U) > 1e-9,
                "the same vertex carries different UVs in different triangles", ref failures);
        }
    }
}
