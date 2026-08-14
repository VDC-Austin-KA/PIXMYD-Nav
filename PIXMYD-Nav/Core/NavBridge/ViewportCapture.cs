using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// Captures the Navisworks main window as a PNG plus a small grayscale
    /// thumbnail, written next to the export so the marker page and AR model can
    /// reference them. Navisworks exposes no managed screenshot API (verified
    /// against 2025.0.0 -- no ExportImage/Screenshot/SaveImage anywhere in the
    /// managed surface), so the composited desktop around the main window handle
    /// is captured via GDI. Every failure degrades to empty strings: a "no photo"
    /// marker is valid output, never an error.
    /// </summary>
    public static class ViewportCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>Path written for the last full image ("" when nothing captured yet).</summary>
        public static string LastFullImage;
        public static string LastMonoThumb;

        /// <summary>
        /// Captures the Navisworks window into <paramref name="folder"/> as
        /// <paramref name="stem"/>.png and <paramref name="stem"/>_mono.png.
        /// Returns the two relative file names, or empty strings on failure.
        /// </summary>
        public static bool Capture(string folder, string stem, int thumbMax = 240)
        {
            LastFullImage = "";
            LastMonoThumb = "";

            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch (Exception) { }

            if (hwnd == IntPtr.Zero) return false;

            RECT rect;
            try
            {
                if (IsIconic(hwnd)) return false; // minimized windows show a blank desktop image
                if (!GetWindowRect(hwnd, out rect)) return false;
            }
            catch (Exception) { return false; }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            try
            {
                Directory.CreateDirectory(folder);

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height),
                            CopyPixelOperation.SourceCopy);
                    }

                    string fullPath = Path.Combine(folder, stem + ".png");
                    bmp.Save(fullPath, ImageFormat.Png);

                    string monoPath = Path.Combine(folder, stem + "_mono.png");
                    using (Bitmap thumb = ScaleMono(bmp, thumbMax))
                        thumb.Save(monoPath, ImageFormat.Png);

                    LastFullImage = fullPath;
                    LastMonoThumb = monoPath;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Downscales preserving aspect, then flattens to grayscale.</summary>
        private static Bitmap ScaleMono(Bitmap source, int maxSide)
        {
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
            int w = Math.Max(1, (int)Math.Round(source.Width * scale));
            int h = Math.Max(1, (int)Math.Round(source.Height * scale));

            var thumb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var matrix = new ColorMatrix(new float[][]
                {
                    new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                    new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                    new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 },
                });
                using (var attrs = new ImageAttributes())
                {
                    attrs.SetColorMatrix(matrix);
                    g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, source.Width, source.Height,
                        GraphicsUnit.Pixel, attrs);
                }
            }
            return thumb;
        }
    }
}