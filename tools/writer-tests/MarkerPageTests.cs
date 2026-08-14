using PIXMY4D_Nav.Core.Markers;
using PIXMY4D_Nav.Core.Points;

namespace PIXMY4D_Nav
{
    internal static class MarkerPageTests
    {
        public static int Run()
        {
            int failures = 0;

            var set = new PointSet { SetId = "b7f3c2e1-0000-0000-0000-000000000000", SetName = "L01 Column Marks" };
            set.Points.Add(new PointRecord
            {
                Id = "P001",
                Label = "Col C-4 base",
                Position = new Vec3(12.4, 8.15, 0.0),
                Grid = new GridInfo { Intersection = "C-4", Level = "L01" },
                Viewpoint = new ViewpointInfo { ThumbMono = "images/P001-mono.png" },
            });
            set.Points.Add(new PointRecord
            {
                Id = "P002",
                Label = "Col C-5 base, no grid",
                Position = new Vec3(20.0, 8.15, 0.0),
            });

            string html = MarkerPage.Render(set);

            Program.Check(html.StartsWith("<!doctype html>"), "starts with doctype", ref failures);
            Program.Check(html.Contains("@media print"), "carries a @media print rule", ref failures);
            Program.Check(html.Contains("page-break-after:always"), "marker sections force a page break", ref failures);
            Program.Check(html.Contains("<svg"), "QR is rendered as inline SVG", ref failures);

            Program.Check(html.Contains(">P001<"), "point id P001 present", ref failures);
            Program.Check(html.Contains("Col C-4 base"), "label P001 present", ref failures);
            Program.Check(html.Contains("12.400, 8.150, 0.000"), "P001 coordinates formatted", ref failures);
            Program.Check(html.Contains("C-4"), "P001 grid intersection text present", ref failures);
            Program.Check(html.Contains("images/P001-mono.png"), "P001 thumbnail img src present", ref failures);

            Program.Check(html.Contains(">P002<"), "point id P002 present", ref failures);
            Program.Check(html.Contains("(no grid loaded)"), "P002 renders without grid text, not an error", ref failures);
            Program.Check(html.Contains("no-photo"), "P002 renders a no-photo placeholder, not a broken img", ref failures);

            // Two points -> two marker sections.
            int count = 0, idx = 0;
            while ((idx = html.IndexOf("class=\"marker\"", idx, System.StringComparison.Ordinal)) >= 0) { count++; idx++; }
            Program.Check(count == 2, "one marker section per point (found " + count + ")", ref failures);

            // Empty set renders without error.
            string emptyHtml = MarkerPage.Render(new PointSet());
            Program.Check(emptyHtml.Contains("</html>"), "empty point set still renders a valid page", ref failures);

            return failures;
        }
    }
}
