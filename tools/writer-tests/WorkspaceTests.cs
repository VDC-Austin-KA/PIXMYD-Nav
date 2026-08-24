using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PIXMYD_Nav.Core.Ar;
using PIXMYD_Nav.Core.Json;
using PIXMYD_Nav.Core.Markers;
using PIXMYD_Nav.Core.Points;
using PIXMYD_Nav.Core.Workspace;

namespace PIXMYD_Nav
{
    /// <summary>
    /// Where files go, and what is in them.
    ///
    /// The folder layout is tested because the previous one put arriving
    /// captures in %TEMP%, where Windows eventually deleted the only copy of
    /// somebody's site work. A layout is not a cosmetic decision when one of
    /// the directories is volatile.
    ///
    /// The two writers are tested by reading their output back: the DXF line by
    /// line, and the GLB through this plugin's own JSON reader. Neither is
    /// proof that Navisworks or a phone will open the file -- nothing available
    /// on a Linux runner is -- but a structural check catches the mistakes that
    /// actually happen, which are a missing section, a chunk length that does
    /// not match the payload, and a coordinate written in exponent form.
    /// </summary>
    internal static class WorkspaceTests
    {
        public static int Run()
        {
            int failures = 0;

            string root = Path.Combine(Path.GetTempPath(), "pixmyd-workspace-" + Guid.NewGuid().ToString("N"));
            try
            {
                WorkspaceHasTwoSides(root, ref failures);
                ImportsAreDatedAndNeverCollide(root, ref failures);
                MigrationMovesOnlyOurFiles(ref failures);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }

            MarkersCarryPointIdsAsLayers(ref failures);
            MarkerGlyphsAreClosedAndCentred(ref failures);
            DxfCoordinatesAreAlwaysDecimal(ref failures);
            GlbIsAWellFormedContainer(ref failures);
            GlbDescribesWhatItContains(ref failures);
            ArModelRecordsTheUpAxisItStartedFrom(ref failures);

            return failures;
        }

        /// <summary>
        /// The phone cannot bring a points.json coordinate into the AR model's
        /// frame without knowing whether the export turned it. After the turn
        /// upAxis reads "Y" either way, so the pre-turn axis is written beside
        /// it; guessing wrong moves the model by a building's height.
        /// </summary>
        private static void ArModelRecordsTheUpAxisItStartedFrom(ref int failures)
        {
            var turned = new ArModelSet { UpAxis = "Y", SourceUpAxis = "Z" };
            string json = turned.ToJson();
            Program.Check(json.Contains("\"navex:upAxis\":\"Y\""), "the exported axis", ref failures);
            Program.Check(json.Contains("\"navex:sourceUpAxis\":\"Z\""),
                "the axis it started from", ref failures);

            // Nobody set it: fall back to the exported axis rather than to an
            // empty string a reader would have to invent a meaning for.
            var plain = new ArModelSet { UpAxis = "Y" };
            Program.Check(plain.ToJson().Contains("\"navex:sourceUpAxis\":\"Y\""),
                "an unset source axis falls back to the exported one", ref failures);
        }

        // MARK: - Workspace


        private static void WorkspaceHasTwoSides(string root, ref int failures)
        {
            var workspace = new PixmydWorkspace(root).EnsureCreated();

            Program.Check(Directory.Exists(workspace.Export), "EXPORT exists after EnsureCreated", ref failures);
            Program.Check(Directory.Exists(workspace.Import), "IMPORT exists after EnsureCreated", ref failures);
            Program.Check(workspace.Export.EndsWith("EXPORT", StringComparison.Ordinal),
                "the outgoing side is called EXPORT", ref failures);
            Program.Check(workspace.Import.EndsWith("IMPORT", StringComparison.Ordinal),
                "the incoming side is called IMPORT", ref failures);
            Program.Check(workspace.ExportFile("points.json").StartsWith(workspace.Export, StringComparison.Ordinal),
                "export files land inside EXPORT", ref failures);

            // A trailing separator on the root must not produce a doubled one.
            var trailing = new PixmydWorkspace(root + Path.DirectorySeparatorChar);
            Program.Check(trailing.Export == workspace.Export,
                "a trailing separator on the root changes nothing", ref failures);

            bool refused = false;
            try { new PixmydWorkspace("   "); } catch (ArgumentException) { refused = true; }
            Program.Check(refused, "a blank root is refused rather than becoming the current directory", ref failures);
        }

        private static void ImportsAreDatedAndNeverCollide(string root, ref int failures)
        {
            var workspace = new PixmydWorkspace(root).EnsureCreated();
            var when = new DateTime(2026, 8, 23, 14, 15, 2, DateTimeKind.Utc);

            string first = workspace.NewImportFolder("3f9c1a2b-1111-2222-3333-444444444444", when);
            Program.Check(Path.GetFileName(first) == "20260823-141502-3f9c1a2b",
                "an import folder is stamped then short-id'd, got " + Path.GetFileName(first), ref failures);

            // Same second, same id: the second arrival must not land in the
            // first one's folder and overwrite its capture.
            string second = workspace.NewImportFolder("3f9c1a2b-1111-2222-3333-444444444444", when);
            Program.Check(second != first, "a second arrival in the same second gets its own folder", ref failures);
            Program.Check(Directory.Exists(second), "and that folder exists", ref failures);

            string anonymous = workspace.NewImportFolder("", when.AddSeconds(1));
            Program.Check(Path.GetFileName(anonymous) == "20260823-141503",
                "a capture with no id is stamped alone, got " + Path.GetFileName(anonymous), ref failures);

            Program.Check(workspace.ImportFolders().Count == 3, "all three arrivals are listed", ref failures);
            Program.Check(Path.GetFileName(workspace.LatestImport()) == "20260823-141503",
                "the newest arrival comes back first", ref failures);

            // An id from another process becomes a path component, so it is
            // filtered rather than trusted.
            Program.Check(PixmydWorkspace.ShortId("../../etc") == "etc",
                "separators are stripped out of a capture id", ref failures);
            Program.Check(PixmydWorkspace.ShortId(null) == "", "a null id yields nothing", ref failures);
        }

        private static void MigrationMovesOnlyOurFiles(ref int failures)
        {
            string root = Path.Combine(Path.GetTempPath(), "pixmyd-migrate-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "points.json"), "{}");
                File.WriteAllText(Path.Combine(root, "P001_photo.png"), "x");
                File.WriteAllText(Path.Combine(root, "Tower A - RFI 214.pdf"), "x");
                File.WriteAllText(Path.Combine(root, "TowerA.nwd"), "x");

                var workspace = new PixmydWorkspace(root);
                List<string> moved = workspace.MigrateLooseFiles();

                Program.Check(moved.Contains("points.json"), "a loose points.json is migrated", ref failures);
                Program.Check(moved.Contains("P001_photo.png"), "its photos come with it", ref failures);
                Program.Check(!moved.Contains("Tower A - RFI 214.pdf"),
                    "somebody else's PDF is left where they put it", ref failures);
                Program.Check(!moved.Contains("TowerA.nwd"), "and so is the model", ref failures);
                Program.Check(File.Exists(Path.Combine(root, "points.json")),
                    "migration copies rather than moves -- the original is the user's", ref failures);
                Program.Check(File.Exists(Path.Combine(workspace.Export, "points.json")),
                    "and the copy is in EXPORT", ref failures);

                // Running again must not overwrite what is now the live export.
                File.WriteAllText(Path.Combine(workspace.Export, "points.json"), "{\"live\":true}");
                workspace.MigrateLooseFiles();
                Program.Check(File.ReadAllText(Path.Combine(workspace.Export, "points.json")) == "{\"live\":true}",
                    "a second migration leaves the current export alone", ref failures);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        // MARK: - Marker geometry

        private static void MarkersCarryPointIdsAsLayers(ref int failures)
        {
            var ids = new List<string> { "P001", "P002" };
            var positions = new List<Vec3> { new Vec3(1, 2, 3), new Vec3(-4.5, 0, 12.25) };

            string dxf = MarkerDxf.Render(ids, positions, MarkerShape.Sphere, 0.0762);
            List<string> lines = Lines(dxf);

            Program.Check(lines[0] == "0" && lines[1] == "SECTION", "a DXF starts with a section", ref failures);
            Program.Check(dxf.Contains("AC1009"), "written as R12, the most widely readable DXF", ref failures);
            Program.Check(lines[lines.Count - 1] == "EOF", "and ends with EOF", ref failures);
            Program.Check(Count(lines, "ENDSEC") == 3, "header, tables and entities are all closed", ref failures);

            Program.Check(dxf.Contains("PIXMYD_P001") && dxf.Contains("PIXMYD_P002"),
                "each point gets its own named layer", ref failures);
            Program.Check(Count(lines, "LAYER") == 3,
                "the layer table is opened once and holds one entry per point", ref failures);
            Program.Check(Count(lines, "POINT") == 2,
                "each point is also written as a literal DXF POINT", ref failures);

            // The coordinate itself has to survive: this is the deliverable.
            int pointIndex = lines.IndexOf("POINT");
            Program.Check(lines[pointIndex + 4] == "1.000000" &&
                          lines[pointIndex + 6] == "2.000000" &&
                          lines[pointIndex + 8] == "3.000000",
                "the POINT carries the coordinate it was placed at", ref failures);

            // Ids come from a user-editable field and become layer names.
            Program.Check(MarkerDxf.LayerNameFor("Col C-4 / base") == "PIXMYD_COL_C-4___BASE",
                "layer names are filtered to what R12 accepts, got " +
                MarkerDxf.LayerNameFor("Col C-4 / base"), ref failures);
            Program.Check(MarkerDxf.LayerNameFor("").Length > 0, "an empty id still names a layer", ref failures);
            Program.Check(MarkerDxf.LayerNameFor(new string('X', 80)).Length <= 31,
                "and a long one is cut to the 31 characters R12 allows", ref failures);

            // Two ids that differ only in a filtered character must not merge.
            string collided = MarkerDxf.Render(
                new List<string> { "A/B", "A B" },
                new List<Vec3> { new Vec3(), new Vec3(1, 1, 1) },
                MarkerShape.Cross, 0.05);
            Program.Check(Count(Lines(collided), "LAYER") == 3,
                "ids that filter to the same name still get one layer each", ref failures);
        }

        private static void MarkerGlyphsAreClosedAndCentred(ref int failures)
        {
            var centre = new Vec3(10, -20, 5);
            const double diameter = 0.0762;   // three inches

            List<Vec3> sphere = MarkerGlyphs.Build(MarkerShape.Sphere, centre, diameter);
            Program.Check(sphere.Count == 32 * 3, "the sphere is 32 triangles, got " + sphere.Count / 3, ref failures);
            foreach (Vec3 v in sphere)
            {
                double radius = Math.Sqrt(
                    (v.X - centre.X) * (v.X - centre.X) +
                    (v.Y - centre.Y) * (v.Y - centre.Y) +
                    (v.Z - centre.Z) * (v.Z - centre.Z));
                Program.Check(Math.Abs(radius - diameter / 2) < 1e-9,
                    "every sphere vertex sits on the marker radius, got " + radius, ref failures);
            }

            List<Vec3> cross = MarkerGlyphs.Build(MarkerShape.Cross, centre, diameter);
            Program.Check(cross.Count == 24 * 3, "the cross is 24 triangles, got " + cross.Count / 3, ref failures);
            Program.Check(Reach(cross, centre) > diameter / 2 * 0.99,
                "the cross arms reach the marker radius", ref failures);

            // A caller that forgets a size must not produce a zero-sized marker
            // nobody can see.
            Program.Check(MarkerGlyphs.Build(MarkerShape.Sphere, centre, 0).Count == 32 * 3,
                "a zero diameter falls back to the default rather than collapsing", ref failures);
        }

        private static void DxfCoordinatesAreAlwaysDecimal(ref int failures)
        {
            // A coordinate small enough that "R" or "G" formatting would write
            // 1E-05, which half of the DXF readers in existence cannot parse.
            string dxf = MarkerDxf.Render(
                new List<string> { "P001" },
                new List<Vec3> { new Vec3(0.00001, 1e12, -0.5) },
                MarkerShape.Cross, 0.05);

            // Walk the group-code pairs and check every coordinate value.
            // A DXF real has no exponent form, and "1E-05" is exactly how a
            // coordinate becomes unreadable to half the parsers in existence.
            List<string> lines = Lines(dxf);
            int coordinates = 0;
            bool plain = true;
            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i], out code)) continue;
                if (code < 10 || code > 39) continue;
                coordinates++;
                if (!IsPlainDecimal(lines[i + 1])) plain = false;
            }
            Program.Check(coordinates > 0, "the file has coordinates to check", ref failures);
            Program.Check(plain, "every coordinate is written in plain decimal form", ref failures);

            // NaN is how one bad pick would otherwise make the whole file
            // unreadable rather than one marker wrong.
            string withNan = MarkerDxf.Render(
                new List<string> { "P001" },
                new List<Vec3> { new Vec3(double.NaN, 0, 0) },
                MarkerShape.Cross, 0.05);
            Program.Check(withNan.IndexOf("NaN", StringComparison.OrdinalIgnoreCase) < 0,
                "a non-finite coordinate degrades to zero rather than poisoning the file", ref failures);
        }

        // MARK: - GLB

        private static void GlbIsAWellFormedContainer(ref int failures)
        {
            byte[] glb = GlbWriter.Render(Cube(), new GlbWriter.Options { Name = "TowerA" });

            Program.Check(glb.Length >= 20, "a GLB has at least a header and two chunk headers", ref failures);
            Program.Check(BitConverter.ToUInt32(glb, 0) == 0x46546C67, "magic is glTF", ref failures);
            Program.Check(BitConverter.ToUInt32(glb, 4) == 2, "version is 2", ref failures);
            Program.Check(BitConverter.ToUInt32(glb, 8) == (uint)glb.Length,
                "the declared total length is the file length", ref failures);

            uint jsonLength = BitConverter.ToUInt32(glb, 12);
            Program.Check(BitConverter.ToUInt32(glb, 16) == 0x4E4F534A, "the first chunk is JSON", ref failures);
            Program.Check(jsonLength % 4 == 0, "the JSON chunk is padded to four bytes", ref failures);

            int binaryHeader = 20 + (int)jsonLength;
            uint binaryLength = BitConverter.ToUInt32(glb, binaryHeader);
            Program.Check(BitConverter.ToUInt32(glb, binaryHeader + 4) == 0x004E4942,
                "the second chunk is BIN", ref failures);
            Program.Check(binaryLength % 4 == 0, "the BIN chunk is padded to four bytes", ref failures);
            Program.Check(binaryHeader + 8 + binaryLength == glb.Length,
                "and the chunks account for the whole file", ref failures);
        }

        private static void GlbDescribesWhatItContains(ref int failures)
        {
            var options = new GlbWriter.Options { Name = "TowerA", ZUpToYUp = true, Offset = new Vec3(100, 200, 0) };
            byte[] glb = GlbWriter.Render(Cube(), options);

            uint jsonLength = BitConverter.ToUInt32(glb, 12);
            string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLength).TrimEnd(' ');

            JsonValue root = JsonReader.Parse(json);
            Program.Check(root != null && root.Type == JsonValue.Kind.Object,
                "the JSON chunk parses as an object", ref failures);
            if (root == null) return;

            Program.Check(root["asset"]["version"].AsString("") == "2.0", "asset.version is 2.0", ref failures);

            JsonValue accessors = root["accessors"];
            Program.Check(accessors.Count == 3,
                "positions, normals and indices each get an accessor", ref failures);

            // 12 triangles, flat shaded: 36 vertices and 36 indices.
            Program.Check(accessors.At(0)["count"].AsNumber(0) == 36,
                "one vertex per triangle corner, got " + accessors.At(0)["count"].AsNumber(0), ref failures);
            Program.Check(accessors.At(2)["count"].AsNumber(0) == 36, "and one index each", ref failures);
            Program.Check(accessors.At(2)["componentType"].AsNumber(0) == 5125,
                "indices are unsigned int, so a real model does not overflow them", ref failures);

            // POSITION must carry min/max: the spec requires it and every viewer
            // frames the model from it before decoding a triangle.
            double[] min = accessors.At(0)["min"].AsVector(3);
            double[] max = accessors.At(0)["max"].AsVector(3);
            Program.Check(min != null && max != null, "POSITION carries min and max", ref failures);
            if (min == null || max == null) return;

            // The cube spans 0..2 in the source frame, offset by (100, 200, 0)
            // and turned Z-up to Y-up: source Y → glTF +Z via (x,z,−y).
            Program.Check(Math.Abs(min[0] - -100) < 1e-4 && Math.Abs(max[0] - -98) < 1e-4,
                "X is shifted by the applied offset", ref failures);
            Program.Check(Math.Abs(min[1] - 0) < 1e-4 && Math.Abs(max[1] - 2) < 1e-4,
                "the model's Z became glTF's up axis", ref failures);
            Program.Check(Math.Abs(min[2] - 198) < 1e-4 && Math.Abs(max[2] - 200) < 1e-4,
                "and the model's Y became glTF's +Z, got " + min[2] + ".." + max[2], ref failures);

            Program.Check(root["buffers"].At(0)["byteLength"].AsNumber(0) > 0,
                "the buffer declares a length", ref failures);

            // An empty model still has to produce a file that opens.
            byte[] empty = GlbWriter.Render(new MeshSoup(), new GlbWriter.Options());
            Program.Check(BitConverter.ToUInt32(empty, 0) == 0x46546C67,
                "an empty soup still writes a valid container", ref failures);
        }

        // MARK: - Fixtures

        /// <summary>A 2 m cube from the origin, as 12 triangles.</summary>
        private static MeshSoup Cube()
        {
            var soup = new MeshSoup();
            var c = new Vec3[]
            {
                new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 2, 0), new Vec3(0, 2, 0),
                new Vec3(0, 0, 2), new Vec3(2, 0, 2), new Vec3(2, 2, 2), new Vec3(0, 2, 2)
            };
            var faces = new int[][]
            {
                new[] { 0, 2, 1 }, new[] { 0, 3, 2 },
                new[] { 4, 5, 6 }, new[] { 4, 6, 7 },
                new[] { 0, 1, 5 }, new[] { 0, 5, 4 },
                new[] { 1, 2, 6 }, new[] { 1, 6, 5 },
                new[] { 2, 3, 7 }, new[] { 2, 7, 6 },
                new[] { 3, 0, 4 }, new[] { 3, 4, 7 }
            };
            foreach (int[] f in faces) soup.AddTriangle(c[f[0]], c[f[1]], c[f[2]]);
            return soup;
        }

        private static List<string> Lines(string text)
        {
            var lines = new List<string>(text.Split(new[] { "\r\n" }, StringSplitOptions.None));
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        /// <summary>Digits, one decimal point, and an optional leading minus.</summary>
        private static bool IsPlainDecimal(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            int start = value[0] == '-' ? 1 : 0;
            if (start >= value.Length) return false;
            int points = 0;
            for (int i = start; i < value.Length; i++)
            {
                if (value[i] == '.') { points++; continue; }
                if (!char.IsDigit(value[i])) return false;
            }
            return points <= 1;
        }

        private static int Count(List<string> lines, string value)
        {
            int count = 0;
            foreach (string line in lines) if (line == value) count++;
            return count;
        }

        private static double Reach(List<Vec3> vertices, Vec3 centre)
        {
            double furthest = 0;
            foreach (Vec3 v in vertices)
            {
                double d = Math.Sqrt(
                    (v.X - centre.X) * (v.X - centre.X) +
                    (v.Y - centre.Y) * (v.Y - centre.Y) +
                    (v.Z - centre.Z) * (v.Z - centre.Z));
                if (d > furthest) furthest = d;
            }
            return furthest;
        }
    }
}
