using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Thaw;

/// <summary>
/// Icon artwork for Thaw: an ice-blue shield/disc, a subtle snowflake, and a
/// bright thawing bolt. Drawn with GDI+ at 4x and downscaled for crisp
/// anti-aliasing at every tray size.
/// </summary>
internal static class Icons
{
    private const float SS = 4f;

    // ------------------------------------------------------------------
    // Public entry points
    // ------------------------------------------------------------------

    /// <summary>Creates a multi-size tray icon so Windows chooses a sharp native frame.</summary>
    public static Icon CreateTrayIcon(bool alert)
    {
        var frames = new List<(int, Bitmap)>
        {
            (16, Draw(16, alert)),
            (20, Draw(20, alert)),
            (24, Draw(24, alert)),
            (32, Draw(32, alert)),
            (48, Draw(48, alert)),
        };
        using var ms = new MemoryStream();
        try
        {
            WriteIco(ms, frames);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            foreach (var (_, bmp) in frames) bmp.Dispose();
        }
    }

    /// <summary>Dev helper: writes PNG previews of both icon variants (for visual review).</summary>
    public static void EmitPngPreview(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (int size in new[] { 16, 32, 64, 128 })
        {
            using var bmp = Draw(size, alert: false);
            bmp.Save(Path.Combine(dir, $"normal-{size}.png"), ImageFormat.Png);
            using var bmp2 = Draw(size, alert: true);
            bmp2.Save(Path.Combine(dir, $"alert-{size}.png"), ImageFormat.Png);
        }
    }

    /// <summary>Writes a multi-size .ico file (used for the exe/app icon).</summary>
    public static void EmitIconFile(string path)
    {
        var frames = new List<(int, Bitmap)>();
        try
        {
            foreach (int size in new[] { 16, 24, 32, 48, 64, 256 })
                frames.Add((size, Draw(size, alert: false)));
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            WriteIco(fs, frames);
        }
        finally
        {
            foreach (var (_, bmp) in frames) bmp.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------

    public static Bitmap Draw(int size, bool alert)
    {
        int big = (int)(size * SS);
        var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            float s = big; // unit = 1/10 of canvas in px

            // ---- outer shadow and icy halo ----
            using (var shadowPen = new Pen(Color.FromArgb(85, 0, 6, 24), s * 0.032f))
                g.DrawEllipse(shadowPen, s * 0.030f, s * 0.038f, s * 0.940f, s * 0.940f);
            using (var halo = new Pen(Color.FromArgb(120, 96, 220, 255), s * 0.012f))
                g.DrawEllipse(halo, s * 0.025f, s * 0.025f, s * 0.950f, s * 0.950f);

            // ---- ice disc ----
            var disc = new RectangleF(s * 0.045f, s * 0.045f, s * 0.910f, s * 0.910f);
            using (var bg = new LinearGradientBrush(
                       disc,
                       Color.FromArgb(255, 0x4D, 0xD7, 0xFF),  // top: frozen cyan
                       Color.FromArgb(255, 0x0B, 0x2B, 0x72),  // bottom: deep ice navy
                       LinearGradientMode.Vertical))
            {
                g.FillEllipse(bg, disc);
            }

            // ---- soft glass highlight ----
            using (var gloss = new LinearGradientBrush(
                       new RectangleF(s * 0.12f, s * 0.08f, s * 0.54f, s * 0.36f),
                       Color.FromArgb(82, 255, 255, 255),
                       Color.FromArgb(0, 255, 255, 255),
                       LinearGradientMode.Vertical))
                g.FillEllipse(gloss, s * 0.11f, s * 0.08f, s * 0.58f, s * 0.38f);

            // ---- double ice rim ----
            using (var rim = new Pen(Color.FromArgb(200, 224, 250, 255), s * 0.014f))
                g.DrawEllipse(rim, disc);
            using (var innerRim = new Pen(Color.FromArgb(70, 120, 235, 255), s * 0.008f))
                g.DrawEllipse(innerRim, s * 0.090f, s * 0.090f, s * 0.820f, s * 0.820f);

            // ---- recognizable snowflake behind the bolt ----
            DrawSnowflake(g, s, size);

            // ---- alert ring ----
            if (alert)
            {
                using var glow = new Pen(Color.FromArgb(95, 0xFF, 0x45, 0x45), s * 0.060f);
                g.DrawEllipse(glow, s * 0.055f, s * 0.055f, s * 0.890f, s * 0.890f);
                using var ring = new Pen(Color.FromArgb(245, 0xFF, 0x63, 0x63), s * 0.026f);
                g.DrawEllipse(ring, s * 0.055f, s * 0.055f, s * 0.890f, s * 0.890f);
            }

            // ---- lightning bolt ----
            DrawBolt(g, s, alert);
        }

        // Downscale with bicubic for a crisp, anti-aliased result.
        var final = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(canvas, 0, 0, size, size);
        }
        canvas.Dispose();
        return final;
    }

    private static void DrawSnowflake(Graphics g, float s, int size)
    {
        // A clean six-arm crystal reads at tray scale better than long random
        // cracks, while the bolt crossing it still communicates "thaw".
        float radius = size <= 16 ? 0.27f : 0.34f;
        float start = size <= 16 ? 0.07f : 0.08f;
        using var pen = new Pen(Color.FromArgb(size <= 16 ? 150 : 175, 224, 249, 255), s * 0.014f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var branch = new Pen(Color.FromArgb(size <= 16 ? 115 : 145, 194, 239, 255), s * 0.010f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        PointF Polar(double angle, float distance) => new(
            s * (0.5f + (float)Math.Cos(angle) * distance),
            s * (0.5f + (float)Math.Sin(angle) * distance));

        for (int i = 0; i < 6; i++)
        {
            double angle = -Math.PI / 2d + i * Math.PI / 3d;
            PointF inner = Polar(angle, start);
            PointF outer = Polar(angle, radius);
            g.DrawLine(pen, inner, outer);

            float branchStart = radius * 0.61f;
            float branchLength = radius * 0.22f;
            PointF branchOrigin = Polar(angle, branchStart);
            g.DrawLine(branch, branchOrigin, Polar(angle + Math.PI / 3d, branchStart - branchLength));
            g.DrawLine(branch, branchOrigin, Polar(angle - Math.PI / 3d, branchStart - branchLength));
        }

        using var core = new SolidBrush(Color.FromArgb(190, 224, 249, 255));
        g.FillEllipse(core, s * 0.465f, s * 0.465f, s * 0.070f, s * 0.070f);
    }

    private static void DrawBolt(Graphics g, float s, bool alert)
    {
        PointF[] bolt =
        {
            new(s * 0.64f, s * 0.13f),
            new(s * 0.40f, s * 0.47f),
            new(s * 0.55f, s * 0.47f),
            new(s * 0.38f, s * 0.88f),
            new(s * 0.68f, s * 0.45f),
            new(s * 0.52f, s * 0.45f),
        };

        // shadow under the bolt for depth
        var shadow = bolt.Select(p => new PointF(p.X + s * 0.012f, p.Y + s * 0.020f)).ToArray();
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPolygon(sh, shadow);

        var top = alert ? Color.FromArgb(255, 0xFF, 0xA3, 0xA3) : Color.FromArgb(255, 0xFD, 0xE6, 0x8A);
        var bottom = alert ? Color.FromArgb(255, 0xDC, 0x26, 0x26) : Color.FromArgb(255, 0xF5, 0x9E, 0x0B);
        using (var fill = new LinearGradientBrush(
                   new RectangleF(s * 0.38f, s * 0.13f, s * 0.30f, s * 0.75f),
                   top, bottom, LinearGradientMode.Vertical))
        {
            g.FillPolygon(fill, bolt);
        }

        using (var outline = new Pen(alert ? Color.FromArgb(255, 0x7F, 0x1D, 0x1D) : Color.FromArgb(255, 0x92, 0x40, 0x0E), s * 0.007f))
            g.DrawPolygon(outline, bolt);

        // inner white-hot core
        float cx = 0.528f * s, cy = 0.477f * s;
        var core = bolt.Select(p => new PointF(cx + (p.X - cx) * 0.40f, cy + (p.Y - cy) * 0.40f)).ToArray();
        using (var hot = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            g.FillPolygon(hot, core);
    }

    // ------------------------------------------------------------------
    // ICO writer (BMP DIB entries + PNG for 256)
    // ------------------------------------------------------------------

    private static void WriteIco(Stream s, IReadOnlyList<(int Size, Bitmap Bmp)> frames)
    {
        var entries = new List<(int Size, byte[] Data, bool IsPng)>();
        foreach (var (size, bmp) in frames)
        {
            if (size >= 256)
                entries.Add((size, ToPng(bmp), true));
            else
                entries.Add((size, ToDib(bmp), false));
        }

        using var bw = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write((ushort)0);          // reserved
        bw.Write((ushort)1);          // icon type
        bw.Write((ushort)entries.Count);

        int offset = 6 + 16 * entries.Count;
        foreach (var (size, data, _) in entries)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);        // palette
            bw.Write((byte)0);        // reserved
            bw.Write((ushort)1);      // planes
            bw.Write((ushort)32);     // bpp
            bw.Write(data.Length);
            bw.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data, _) in entries)
            bw.Write(data);
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        var rect = new Rectangle(0, 0, w, h);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bd.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bmp.UnlockBits(bd);
        }

        int maskStride = ((w + 31) / 32) * 4;
        var andMask = new byte[maskStride * h]; // all-zero: transparency comes from the alpha channel

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);                       // BITMAPINFOHEADER size
        bw.Write(w);
        bw.Write(h * 2);                    // XOR + AND planes
        bw.Write((ushort)1);                // planes
        bw.Write((ushort)32);               // bpp
        bw.Write(0);                        // BI_RGB
        bw.Write(stride * h + andMask.Length);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)    // DIB rows are bottom-up; GDI+ is top-down
            bw.Write(pixels, y * stride, stride);
        bw.Write(andMask);
        return ms.ToArray();
    }
}
