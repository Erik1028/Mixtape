using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A gallery of pre-designed, selectable cover arts for playlists and the library. Each art is
/// generated deterministically from its id (so it's stable and needs no asset files) but uses
/// hand-tuned styles — gradients, rings, stripes, dots, peaks, blobs — across the colour wheel,
/// so they read as intentional artwork rather than the plain ♪ placeholder tile.
/// </summary>
internal static class CoverArt
{
    public const int Count = 24;
    private const int Styles = 8;

    private static readonly Dictionary<(int Id, int Size), Bitmap> Cache = new();

    public static Bitmap Generate(int id, int size)
    {
        id = ((id % Count) + Count) % Count;
        var key = (id, size);
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double h = id * (360.0 / Count);
            float r = Math.Max(3, size * Theme.TileFrac);
            using var clip = Theme.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), r);
            g.SetClip(clip);
            Paint(g, id % Styles, h, size);
            g.ResetClip();
            using var ip = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), r);
            using var pen = new Pen(Color.FromArgb(34, 255, 255, 255));
            g.DrawPath(pen, ip);
        }
        Cache[key] = bmp;
        return bmp;
    }

    private static void Paint(Graphics g, int style, double h, int s)
    {
        Color c1 = Theme.HsvToColor(h, 0.58, 0.88);
        Color c2 = Theme.HsvToColor(h + 28, 0.70, 0.52);
        Color c3 = Theme.HsvToColor(h - 34, 0.52, 0.97);
        Color dark = Theme.HsvToColor(h + 10, 0.65, 0.26);
        var full = new Rectangle(0, 0, s, s);

        switch (style)
        {
            case 0: // diagonal gradient
                using (var b = new LinearGradientBrush(full, c1, c2, Theme.ArtAngle)) g.FillRectangle(b, full);
                break;

            case 1: // radial glow
                g.Clear(dark);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(-s * 0.2f, -s * 0.2f, s * 1.4f, s * 1.4f);
                    using var pgb = new PathGradientBrush(path) { CenterPoint = new PointF(s * 0.38f, s * 0.34f), CenterColor = c3, SurroundColors = new[] { c2 } };
                    g.FillRectangle(pgb, full);
                }
                break;

            case 2: // duotone with offset discs
                g.Clear(c2);
                using (var b = new SolidBrush(c1)) g.FillEllipse(b, s * 0.18f, s * 0.22f, s * 0.9f, s * 0.9f);
                using (var b = new SolidBrush(Color.FromArgb(150, c3))) g.FillEllipse(b, s * 0.5f, -s * 0.15f, s * 0.55f, s * 0.55f);
                break;

            case 3: // diagonal stripes
                using (var b = new LinearGradientBrush(full, c2, dark, Theme.ArtAngle)) g.FillRectangle(b, full);
                using (var pen = new Pen(Color.FromArgb(210, c1), s * 0.10f))
                {
                    for (int i = -s; i < s * 2; i += (int)(s * 0.26f)) g.DrawLine(pen, i, 0, i + s, s);
                }
                break;

            case 4: // concentric rings
                g.Clear(dark);
                for (int i = 6; i >= 1; i--)
                {
                    float rad = s * 0.12f * i;
                    using var b = new SolidBrush(i % 2 == 0 ? c1 : c3);
                    g.FillEllipse(b, s / 2f - rad, s / 2f - rad, rad * 2, rad * 2);
                }
                break;

            case 5: // dot grid over a gradient
                using (var b = new LinearGradientBrush(full, c1, c2, Theme.ArtAngle)) g.FillRectangle(b, full);
                using (var b = new SolidBrush(Color.FromArgb(120, c3)))
                {
                    float step = s / 5f, d = s * 0.1f;
                    for (float y = step * 0.6f; y < s; y += step)
                        for (float x = step * 0.6f; x < s; x += step)
                            g.FillEllipse(b, x, y, d, d);
                }
                break;

            case 6: // layered peaks
                using (var b = new LinearGradientBrush(full, c3, c1, Theme.ArtAngle)) g.FillRectangle(b, full);
                Peak(g, c2, s, 0.62f, 0.55f);
                Peak(g, dark, s, 0.78f, 0.30f);
                break;

            default: // 7: soft blobs
                using (var b = new LinearGradientBrush(full, c2, dark, Theme.ArtAngle)) g.FillRectangle(b, full);
                using (var b = new SolidBrush(Color.FromArgb(200, c1))) g.FillEllipse(b, -s * 0.1f, s * 0.45f, s * 0.8f, s * 0.8f);
                using (var b = new SolidBrush(Color.FromArgb(170, c3))) g.FillEllipse(b, s * 0.45f, s * 0.05f, s * 0.7f, s * 0.7f);
                break;
        }
    }

    private static void Peak(Graphics g, Color c, int s, float baseY, float height)
    {
        using var b = new SolidBrush(c);
        var pts = new[] { new PointF(-2, s), new PointF(s * 0.32f, s * (baseY - height)), new PointF(s * 0.66f, s * baseY), new PointF(s + 2, s * (baseY - height * 0.6f)), new PointF(s + 2, s) };
        g.FillPolygon(b, pts);
    }
}
