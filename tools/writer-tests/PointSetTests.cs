using System;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav
{
    internal static class PointSetTests
    {
        public static int Run()
        {
            int failures = 0;

            // Contract shape: contractVersion first, empty (not null) grid strings
            // when no grid system is loaded -- docs/contracts/points.md.
            var set = new PointSet
            {
                SetId = "b7f3c2e1-0000-0000-0000-000000000000",
                SetName = "L01 Column Marks",
                CreatedUtc = new DateTime(2026, 8, 13, 14, 2, 11, DateTimeKind.Utc),
            };
            set.Provenance.SourceDocument = "TowerA.nwd";
            set.Provenance.SourceUnits = "Feet";
            set.Provenance.TargetUnits = "Meters";
            set.Provenance.AppliedOffset = new Vec3(-1204.5, 883.2, 0.0);
            set.Provenance.ExportedUtc = set.CreatedUtc;

            var noGrid = new PointRecord
            {
                Id = "P001",
                Label = "Col C-4 base, no grid loaded",
                Position = new Vec3(12.4, 8.15, 0.0),
            };
            set.Points.Add(noGrid);

            var withGrid = new PointRecord
            {
                Id = "P002",
                Label = "Col C-5 base",
                Position = new Vec3(20.0, 8.15, 0.0),
                Grid = new GridInfo { Intersection = "C-5", Level = "L01", Offset = new Vec3(0.1, 0, 0), Distance = 0.1 },
                Viewpoint = new ViewpointInfo
                {
                    Image = "images/P002.png",
                    ThumbMono = "images/P002-mono.png",
                    Camera = { Position = new Vec3(1, 2, 3), LookAt = new Vec3(4, 5, 6) },
                },
            };
            set.Points.Add(withGrid);

            string json = set.ToJson();

            Program.Check(json.StartsWith("{\"contractVersion\":\"1.0\""), "contractVersion must be first field", ref failures);
            Program.Check(json.Contains("\"setId\":\"b7f3c2e1-0000-0000-0000-000000000000\""), "setId present", ref failures);
            Program.Check(json.Contains("\"createdUtc\":\"2026-08-13T14:02:11.000Z\""), "createdUtc ISO8601 format", ref failures);
            Program.Check(json.Contains("\"navex:appliedOffset\":[-1204.5,883.2,0]"), "provenance appliedOffset vector", ref failures);

            // The contract-critical rule: empty strings, never nulls, for an unloaded grid.
            Program.Check(json.Contains("\"id\":\"P001\""), "P001 present", ref failures);
            int p001Index = json.IndexOf("\"id\":\"P001\"", StringComparison.Ordinal);
            int p001GridIndex = json.IndexOf("\"grid\":{\"intersection\":\"\",\"level\":\"\"", p001Index, StringComparison.Ordinal);
            Program.Check(p001GridIndex > p001Index, "P001 grid.intersection/level are empty strings, not null/omitted", ref failures);
            Program.Check(!json.Contains("\"intersection\":null") && !json.Contains("\"level\":null"),
                "grid fields never serialise as null", ref failures);

            // qrPayload is computed from setId + point id, never hand-set.
            Program.Check(json.Contains("\"qrPayload\":\"pixmy://p/b7f3c2e1/P001\""), "P001 qrPayload", ref failures);
            Program.Check(json.Contains("\"qrPayload\":\"pixmy://p/b7f3c2e1/P002\""), "P002 qrPayload", ref failures);

            // P001 has no viewpoint yet (deferred capture) -- field must be omitted, not emitted empty.
            int p002Index = json.IndexOf("\"id\":\"P002\"", StringComparison.Ordinal);
            string p001Slice = json.Substring(p001Index, p002Index - p001Index);
            Program.Check(!p001Slice.Contains("\"viewpoint\""), "viewpoint omitted when not captured", ref failures);
            Program.Check(json.Contains("\"thumbMono\":\"images/P002-mono.png\""), "P002 viewpoint.thumbMono", ref failures);

            // Empty point set is valid, not an error.
            var empty = new PointSet { SetId = "abcdefgh" };
            string emptyJson = empty.ToJson();
            Program.Check(emptyJson.Contains("\"points\":[]"), "empty point set serialises to an empty points array", ref failures);

            return failures;
        }
    }
}
