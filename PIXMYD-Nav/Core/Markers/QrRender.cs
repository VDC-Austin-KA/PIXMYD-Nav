using System;
using PIXMYD_Nav.Core.Markers;

namespace PIXMYD_Nav.Core.Markers
{
    /// <summary>
    /// Turns a QrCode module grid into a bitmap for on-screen display.
    ///
    /// MarkerPage already renders a QR as inline SVG for the printable page, and
    /// that is the right output for print. A WPF window cannot show SVG without
    /// a renderer, so the transfer code -- which is read off a monitor, not off
    /// paper -- is drawn as an uncompressed BMP instead. Uncompressed because a
    /// PNG needs a deflate stream and a CRC, and this is fifty lines against a
    /// dependency for an image that is never written to disk.
    ///
    /// The quiet zone matters more than it looks. ISO 18004 requires four
    /// modules of light margin, and a symbol drawn hard against a dark WPF panel
    /// with no margin is the single most common reason a code that looks perfect
    /// on screen will not scan.
    ///
    /// Pure. In WriterTests.csproj.
    /// </summary>
    public static class QrRender
    {
        public const int QuietZoneModules = 4;

        /// <summary>
        /// A 24-bit BMP of the symbol, black on white, scaled by whole modules.
        /// </summary>
        /// <param name="scale">Pixels per module. Below about 4 a phone camera
        /// struggles at arm's length from a monitor.</param>
        public static byte[] ToBmp(QrCode qr, int scale)
        {
            if (qr == null) throw new ArgumentNullException("qr");
            if (scale < 1) scale = 1;

            int modules = qr.Size + QuietZoneModules * 2;
            int side = modules * scale;

            // Each BMP row is padded to a 4-byte boundary.
            int rowBytes = side * 3;
            int padding = (4 - rowBytes % 4) % 4;
            int stride = rowBytes + padding;
            int pixelBytes = stride * side;
            const int headerBytes = 54;

            var bmp = new byte[headerBytes + pixelBytes];

            // BITMAPFILEHEADER
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            WriteInt32(bmp, 2, headerBytes + pixelBytes);
            WriteInt32(bmp, 10, headerBytes);

            // BITMAPINFOHEADER
            WriteInt32(bmp, 14, 40);
            WriteInt32(bmp, 18, side);
            WriteInt32(bmp, 22, side);
            bmp[26] = 1; // planes
            bmp[28] = 24; // bits per pixel
            WriteInt32(bmp, 34, pixelBytes);
            WriteInt32(bmp, 38, 2835); // ~72 dpi
            WriteInt32(bmp, 42, 2835);

            // White ground, then the dark modules. A BMP is stored bottom-up, so
            // row 0 of the file is the last row of the image.
            for (int i = headerBytes; i < bmp.Length; i++) bmp[i] = 0xFF;

            for (int row = 0; row < side; row++)
            {
                int moduleRow = row / scale - QuietZoneModules;
                if (moduleRow < 0 || moduleRow >= qr.Size) continue;

                int fileRow = side - 1 - row;
                int rowStart = headerBytes + fileRow * stride;

                for (int column = 0; column < side; column++)
                {
                    int moduleColumn = column / scale - QuietZoneModules;
                    if (moduleColumn < 0 || moduleColumn >= qr.Size) continue;
                    if (!qr.Modules[moduleRow, moduleColumn]) continue;

                    int offset = rowStart + column * 3;
                    bmp[offset] = 0;
                    bmp[offset + 1] = 0;
                    bmp[offset + 2] = 0;
                }
            }

            return bmp;
        }

        /// <summary>
        /// The symbol as inline SVG, for anywhere HTML is the output.
        ///
        /// One path of rectangles rather than one element per module: a version-3
        /// symbol is 29x29, and 841 elements make a browser's print preview
        /// noticeably slow for no gain.
        /// </summary>
        public static string ToSvg(QrCode qr, int scale)
        {
            if (qr == null) throw new ArgumentNullException("qr");
            if (scale < 1) scale = 1;

            int modules = qr.Size + QuietZoneModules * 2;
            int side = modules * scale;

            var sb = new System.Text.StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(side)
              .Append("\" height=\"").Append(side)
              .Append("\" viewBox=\"0 0 ").Append(modules).Append(' ').Append(modules)
              .Append("\" shape-rendering=\"crispEdges\">");
            sb.Append("<rect width=\"").Append(modules).Append("\" height=\"").Append(modules)
              .Append("\" fill=\"#fff\"/>");
            sb.Append("<path fill=\"#000\" d=\"");
            for (int r = 0; r < qr.Size; r++)
            {
                for (int c = 0; c < qr.Size; c++)
                {
                    if (!qr.Modules[r, c]) continue;
                    sb.Append('M').Append(c + QuietZoneModules).Append(' ').Append(r + QuietZoneModules)
                      .Append("h1v1h-1z");
                }
            }
            sb.Append("\"/></svg>");
            return sb.ToString();
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
