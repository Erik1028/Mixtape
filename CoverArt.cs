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
    /// <summary>Reserved id for the "mixtape cassette" style (well clear of 0..23 and of -1 = automatic).
    /// Persisted in settings like any other cover id; rendered via <see cref="GenerateTitled"/> with the
    /// playlist/library name printed on the tape label.</summary>
    public const int CassetteId = 100;

    private static readonly Dictionary<(int Id, int Size), Bitmap> Cache = new();
    private static readonly Dictionary<(int Size, string Title), Bitmap> CassetteCache = new();

    public static Bitmap Generate(int id, int size) => GenerateTitled(id, size, null);

    /// <summary>Like <see cref="Generate"/>, but the cassette style (<see cref="CassetteId"/>) prints
    /// <paramref name="title"/> on its label. Non-cassette ids ignore the title.</summary>
    public static Bitmap GenerateTitled(int id, int size, string? title)
    {
        if (id == CassetteId)
        {
            string t = title ?? "";
            var ck = (size, t);
            if (CassetteCache.TryGetValue(ck, out var ch)) return ch;
            // Keyed by free-text title, so unlike the bounded style cache this could grow with renamed playlists.
            // Cap it; callers clone (header) or hold their own ref (sidebar) so dropping cache refs is safe.
            if (CassetteCache.Count >= 128) CassetteCache.Clear();
            var cb = RenderInto(size, (g, s) => PaintCassette(g, s, t));
            CassetteCache[ck] = cb;
            return cb;
        }
        id = ((id % Count) + Count) % Count;
        var key = (id, size);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var bmp = RenderInto(size, (g, s) => Paint(g, id % Styles, id * (360.0 / Count), s));
        Cache[key] = bmp;
        return bmp;
    }

    // Shared chrome for every cover: rounded-clip the body paint, then stroke a faint inner frame.
    private static Bitmap RenderInto(int size, Action<Graphics, int> body)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float r = Math.Max(3, size * Theme.TileFrac);
        using (var clip = Theme.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), r))
        {
            g.SetClip(clip);
            body(g, size);
            g.ResetClip();
        }
        using var ip = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), r);
        using var pen = new Pen(Color.FromArgb(34, 255, 255, 255));
        g.DrawPath(pen, ip);
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

    // The "mixtape" cassette: a cream shell with the title on the label, a dark tape window with two reels,
    // and corner screws, over a tinted backdrop. Hue derives from the title so each playlist gets its own colour.
    private static void PaintCassette(Graphics g, int s, string title)
    {
        double h = string.IsNullOrEmpty(title) ? 168 : (Theme.StableHash(title) % 360 + 360) % 360;
        var full = new Rectangle(0, 0, s, s);
        using (var b = new LinearGradientBrush(full, Theme.HsvToColor(h, 0.52, 0.44), Theme.HsvToColor(h + 16, 0.66, 0.22), Theme.ArtAngle)) g.FillRectangle(b, full);

        float bw = s * 0.80f, bh = s * 0.54f, bx = (s - bw) / 2f, by = (s - bh) / 2f;
        var body = new RectangleF(bx, by, bw, bh);
        Color shell = Theme.HsvToColor(h, 0.10, 0.93);
        using (var sb = new SolidBrush(shell)) using (var bp = Theme.RoundedRect(body, s * 0.045f)) g.FillPath(sb, bp);

        // label
        float lx = bx + bw * 0.09f, ly = by + bh * 0.09f, lw = bw * 0.82f, lh = bh * 0.30f;
        var label = new RectangleF(lx, ly, lw, lh);
        using (var lb = new SolidBrush(Color.White)) using (var lp = Theme.RoundedRect(label, s * 0.018f)) g.FillPath(lb, lp);
        Color accent = Theme.HsvToColor(h, 0.78, 0.72);
        using (var st = new SolidBrush(accent)) g.FillRectangle(st, lx, ly, lw, Math.Max(2f, lh * 0.20f));
        if (s >= 44 && !string.IsNullOrEmpty(title))
        {
            using var f = Theme.UiFont(Math.Max(7f, s * 0.052f), FontStyle.Bold);
            var tr = Rectangle.Round(new RectangleF(lx + lw * 0.05f, ly + lh * 0.24f, lw * 0.90f, lh * 0.74f));
            TextRenderer.DrawText(g, title, f, tr, Theme.HsvToColor(h, 0.55, 0.24),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis | TextFormatFlags.NoPrefix);
        }

        // tape window + reels
        float ww = bw * 0.74f, wh = bh * 0.34f, wx = bx + (bw - ww) / 2f, wy = by + bh * 0.52f;
        using (var wb = new SolidBrush(Theme.HsvToColor(h, 0.42, 0.13))) using (var wp = Theme.RoundedRect(new RectangleF(wx, wy, ww, wh), s * 0.02f)) g.FillPath(wb, wp);
        float reelR = wh * 0.36f, ry = wy + wh / 2f;
        Reel(g, wx + ww * 0.27f, ry, reelR, shell);
        Reel(g, wx + ww * 0.73f, ry, reelR, shell);

        // corner screws
        using var sc = new SolidBrush(Theme.HsvToColor(h, 0.08, 0.66));
        float sr = Math.Max(1.2f, s * 0.009f);
        foreach (var (px, py) in new[] { (bx + bw * 0.05f, by + bh * 0.08f), (bx + bw * 0.95f, by + bh * 0.08f), (bx + bw * 0.05f, by + bh * 0.92f), (bx + bw * 0.95f, by + bh * 0.92f) })
            g.FillEllipse(sc, px - sr, py - sr, sr * 2, sr * 2);
    }

    private static void Reel(Graphics g, float cx, float cy, float r, Color hub)
    {
        using (var b = new SolidBrush(Color.FromArgb(255, 66, 60, 54))) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        using var pen = new Pen(Color.FromArgb(130, 28, 26, 23), Math.Max(1f, r * 0.16f));
        for (int i = 0; i < 6; i++) { double a = i * Math.PI / 3; g.DrawLine(pen, cx, cy, cx + (float)Math.Cos(a) * r * 0.92f, cy + (float)Math.Sin(a) * r * 0.92f); }
        using (var hb = new SolidBrush(hub)) g.FillEllipse(hb, cx - r * 0.36f, cy - r * 0.36f, r * 0.72f, r * 0.72f);
    }
}
