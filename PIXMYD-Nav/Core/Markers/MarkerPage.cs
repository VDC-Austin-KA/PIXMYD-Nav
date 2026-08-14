using System;
using System.Globalization;
using System.Text;
using PIXMYD_Nav.Core.Points;

namespace PIXMYD_Nav.Core.Markers
{
    /// <summary>
    /// Renders one printable page per point as a single self-contained HTML file:
    /// QR code, point id and label, coordinates, nearest grid intersection, and an
    /// &lt;img&gt; slot for the mono thumbnail. HTML, not PDF, deliberately -- see
    /// docs/work-orders/pixmy4d-nav.md task P2: hand-rolling a PDF writer to print a
    /// page of boxes is real work for no gain when a browser already does it via
    /// @media print. The QR is inline SVG built straight from QrEncoder's module
    /// grid, so there is no image encoder dependency either.
    /// </summary>
    public static class MarkerPage
    {
        public static string Render(PointSet set)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
            sb.Append("<title>").Append(Html(set.SetName)).Append(" -- Field Markers</title>");
            sb.Append("<style>").Append(Css).Append("</style></head><body>");

            foreach (PointRecord point in set.Points)
                sb.Append(RenderPoint(set, point));

            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string RenderPoint(PointSet set, PointRecord point)
        {
            QrCode qr = QrEncoder.Encode(set.QrPayloadFor(point));

            var sb = new StringBuilder();
            sb.Append("<section class=\"marker\">");
            sb.Append("<div class=\"qr\">").Append(QrSvg(qr)).Append("</div>");
            sb.Append("<div class=\"info\">");
            sb.Append("<h1>").Append(Html(point.Id)).Append("</h1>");
            sb.Append("<p class=\"label\">").Append(Html(point.Label)).Append("</p>");
            sb.Append("<table>");
            AppendRow(sb, "Position", FormatVec(point.Position));
            AppendRow(sb, "Grid intersection",
                string.IsNullOrEmpty(point.Grid.Intersection) ? "(no grid loaded)" : point.Grid.Intersection);
            AppendRow(sb, "Level",
                string.IsNullOrEmpty(point.Grid.Level) ? "(unknown)" : point.Grid.Level);
            sb.Append("</table>");
            sb.Append("</div>");

            string thumb = point.Viewpoint != null ? point.Viewpoint.ThumbMono : null;
            if (string.IsNullOrEmpty(thumb) && point.Viewpoint != null) thumb = point.Viewpoint.Image;
            sb.Append("<div class=\"photo\">");
            if (!string.IsNullOrEmpty(thumb))
                sb.Append("<img src=\"").Append(Html(thumb)).Append("\" alt=\"Reference photo for ")
                  .Append(Html(point.Id)).Append("\">");
            else
                sb.Append("<div class=\"no-photo\">no photo</div>");
            sb.Append("</div>");
            sb.Append("</section>");
            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, string label, string value)
        {
            sb.Append("<tr><th>").Append(Html(label)).Append("</th><td>").Append(Html(value)).Append("</td></tr>");
        }

        private static string FormatVec(Vec3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.000}, {1:0.000}, {2:0.000}", v.X, v.Y, v.Z);
        }

        // Renders the QR module grid as inline SVG -- no PNG/bitmap dependency.
        private static string QrSvg(QrCode qr)
        {
            const int quietZone = 4; // module-widths of light border, per ISO 18004
            int dim = qr.Size + quietZone * 2;

            var sb = new StringBuilder();
            sb.Append("<svg viewBox=\"0 0 ").Append(dim).Append(' ').Append(dim)
              .Append("\" width=\"200\" height=\"200\" shape-rendering=\"crispEdges\">");
            sb.Append("<rect width=\"").Append(dim).Append("\" height=\"").Append(dim).Append("\" fill=\"#fff\"/>");

            for (int r = 0; r < qr.Size; r++)
                for (int c = 0; c < qr.Size; c++)
                    if (qr.Modules[r, c])
                        sb.Append("<rect x=\"").Append(c + quietZone).Append("\" y=\"").Append(r + quietZone)
                          .Append("\" width=\"1\" height=\"1\" fill=\"#000\"/>");

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private const string Css =
            "body{font-family:Arial,Helvetica,sans-serif;margin:0}" +
            ".marker{display:flex;align-items:center;gap:24px;padding:32px;page-break-after:always}" +
            ".marker:last-child{page-break-after:auto}" +
            ".qr{flex:0 0 auto}" +
            ".info{flex:1 1 auto}" +
            ".info h1{margin:0 0 4px;font-size:28px}" +
            ".label{margin:0 0 12px;color:#444;font-size:16px}" +
            "table{border-collapse:collapse}" +
            "th{text-align:left;color:#666;font-weight:normal;padding:2px 12px 2px 0}" +
            "td{padding:2px 0;font-family:Consolas,monospace}" +
            ".photo{flex:0 0 160px;height:160px;border:1px solid #ccc;display:flex;align-items:center;justify-content:center}" +
            ".photo img{max-width:100%;max-height:100%}" +
            ".no-photo{color:#999;font-size:12px}" +
            "@media print{.marker{padding:24px}}";
    }
}
