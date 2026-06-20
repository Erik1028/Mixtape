using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Generates a pack of era-correct "wallpaper" backdrops (4:3, any size) to add to an iPod's Photos library —
/// brushed metal, vinyl, cassette, aurora gradients, a click wheel, carbon weave. Pure GDI+, deterministic.
/// </summary>
internal static class Wallpaper
{
    public static readonly string[] Names =
    {
        "Brushed Silver", "Graphite", "Vinyl", "Cassette", "Aurora", "Sunset", "Click Wheel", "Carbon",
    };
    public static int Count => Names.Length;

    public static Bitmap Render(int index, int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        switch (index)
        {
            case 0: Brushed(g, w, h, Color.FromArgb(198, 201, 206)); break;
            case 1: Brushed(g, w, h, Color.FromArgb(68, 71, 77)); break;
            case 2: Vinyl(g, w, h); break;
            case 3: Cassette(g, w, h); break;
            case 4: Aurora(g, w, h, new[] { 188, 205, 160 }); break;
            case 5: Aurora(g, w, h, new[] { 18, 330, 285 }); break;
            case 6: ClickWheel(g, w, h); break;
            default: Carbon(g, w, h); break;
        }
        return bmp;
    }

    // ---- brushed aluminium: vertical gradient + fine horizontal streaks + a top sheen ----
    private static void Brushed(Graphics g, int w, int h, Color baseCol)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Blend(baseCol, Color.White, 0.18), Theme.Blend(baseCol, Color.Black, 0.24), 90f))
            g.FillRectangle(bg, 0, 0, w, h);
        var rnd = new Random(31);
        for (int y = 0; y < h; y++)
        {
            int a = rnd.Next(0, 20);
            if (a > 3) using (var p = new Pen(Color.FromArgb(a, 255, 255, 255))) g.DrawLine(p, 0, y, w, y);
            int d = rnd.Next(0, 18);
            if (d > 4) using (var p = new Pen(Color.FromArgb(d, 0, 0, 0))) g.DrawLine(p, 0, y, w, y);
        }
        using (var sheen = new LinearGradientBrush(new Rectangle(0, 0, w, Math.Max(1, h / 3)), Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillRectangle(sheen, 0, 0, w, h / 3);
    }

    // ---- vinyl record: black disc, grooves, accent label, diagonal sheen ----
    private static void Vinyl(Graphics g, int w, int h)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(20, 20, 23), Color.FromArgb(8, 8, 9), 90f)) g.FillRectangle(bg, 0, 0, w, h);
        float cx = w / 2f, cy = h / 2f, R = Math.Min(w, h) * 0.44f;
        using (var disc = new SolidBrush(Color.FromArgb(13, 13, 14))) g.FillEllipse(disc, cx - R, cy - R, R * 2, R * 2);
        using (var gp = new Pen(Color.FromArgb(20, 185, 190, 200), 1f))
            for (float r = R * 0.34f; r < R; r += Math.Max(2f, R * 0.012f)) g.DrawEllipse(gp, cx - r, cy - r, r * 2, r * 2);
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(cx - R, cy - R, R * 2, R * 2);
            var save = g.Clip; g.SetClip(clip, CombineMode.Intersect);
            using (var sh = new LinearGradientBrush(new RectangleF(cx - R, cy - R, R * 2, R * 2), Color.FromArgb(50, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 35f))
                g.FillRectangle(sh, cx - R, cy - R, R * 2, R * 2);
            g.Clip = save;
        }
        float lr = R * 0.33f;
        using (var lab = new SolidBrush(Theme.Accent)) g.FillEllipse(lab, cx - lr, cy - lr, lr * 2, lr * 2);
        using (var ring = new Pen(Color.FromArgb(70, 0, 0, 0), 2f)) g.DrawEllipse(ring, cx - lr, cy - lr, lr * 2, lr * 2);
        using (var hole = new SolidBrush(Color.FromArgb(10, 10, 11))) g.FillEllipse(hole, cx - 3.5f, cy - 3.5f, 7, 7);
        using (var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 2f)) g.DrawEllipse(edge, cx - R, cy - R, R * 2, R * 2);
    }

    // ---- cassette tape (landscape) ----
    private static void Cassette(Graphics g, int w, int h)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Blend(Theme.Accent, Color.Black, 0.55), Color.FromArgb(13, 14, 18), 90f)) g.FillRectangle(bg, 0, 0, w, h);
        float m = Math.Min(w, h) * 0.14f;
        var shell = new RectangleF(m, h * 0.20f, w - 2 * m, h * 0.60f);
        float rad = shell.Height * 0.12f;
        using (var sb = new SolidBrush(Theme.Blend(Theme.Accent, Color.Black, 0.28))) using (var sp = Theme.RoundedRect(shell, rad)) g.FillPath(sb, sp);
        using (var ed = new Pen(Color.FromArgb(55, 255, 255, 255))) using (var sp = Theme.RoundedRect(shell, rad)) g.DrawPath(ed, sp);
        var label = new RectangleF(shell.X + shell.Width * 0.08f, shell.Y + shell.Height * 0.12f, shell.Width * 0.84f, shell.Height * 0.32f);
        using (var lb = new SolidBrush(Color.FromArgb(236, 238, 240))) using (var lp = Theme.RoundedRect(label, 6)) g.FillPath(lb, lp);
        using (var ln = new Pen(Color.FromArgb(40, 0, 0, 0)))
            for (int i = 1; i <= 2; i++) { float ly = label.Y + label.Height * (0.32f + 0.26f * i); g.DrawLine(ln, label.X + 10, ly, label.Right - 10, ly); }
        float ry = shell.Y + shell.Height * 0.68f, rr = shell.Height * 0.15f;
        float lx = shell.X + shell.Width * 0.32f, rx = shell.X + shell.Width * 0.68f;
        var win = new RectangleF(lx - rr * 1.6f, ry - rr * 1.4f, (rx + rr * 1.6f) - (lx - rr * 1.6f), rr * 2.8f);
        using (var wb = new SolidBrush(Color.FromArgb(34, 0, 0, 0))) using (var wp = Theme.RoundedRect(win, 8)) g.FillPath(wb, wp);
        foreach (float x in new[] { lx, rx })
        {
            using var rb = new SolidBrush(Color.FromArgb(246, 246, 248)); g.FillEllipse(rb, x - rr, ry - rr, rr * 2, rr * 2);
            using var teeth = new Pen(Color.FromArgb(60, 0, 0, 0), 1.4f);
            for (int t = 0; t < 6; t++) { double an = t * Math.PI / 3; g.DrawLine(teeth, x, ry, x + (float)Math.Cos(an) * rr, ry + (float)Math.Sin(an) * rr); }
            using var hb = new SolidBrush(Theme.Blend(Theme.Accent, Color.Black, 0.30)); g.FillEllipse(hb, x - rr * 0.42f, ry - rr * 0.42f, rr * 0.84f, rr * 0.84f);
        }
    }

    // ---- aurora: soft radial colour blooms over near-black ----
    private static void Aurora(Graphics g, int w, int h, int[] hues)
    {
        using (var bg = new SolidBrush(Color.FromArgb(12, 13, 18))) g.FillRectangle(bg, 0, 0, w, h);
        var pts = new[] { new PointF(w * 0.26f, h * 0.30f), new PointF(w * 0.76f, h * 0.40f), new PointF(w * 0.50f, h * 0.80f) };
        for (int i = 0; i < hues.Length && i < pts.Length; i++)
        {
            float R = Math.Min(w, h) * 0.62f;
            var c = Theme.HsvToColor(hues[i], 0.62, 0.88);
            using var path = new GraphicsPath();
            path.AddEllipse(pts[i].X - R, pts[i].Y - R, R * 2, R * 2);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(155, c),
                SurroundColors = new[] { Color.FromArgb(0, c) },
                CenterPoint = pts[i],
            };
            g.FillEllipse(pgb, pts[i].X - R, pts[i].Y - R, R * 2, R * 2);
        }
    }

    // ---- a clean click-wheel emblem ----
    private static void ClickWheel(Graphics g, int w, int h)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(30, 32, 38), Color.FromArgb(12, 13, 16), 90f)) g.FillRectangle(bg, 0, 0, w, h);
        float cx = w / 2f, cy = h / 2f, R = Math.Min(w, h) * 0.34f, ir = R * 0.36f;
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(sh, cx - R, cy - R + R * 0.06f, R * 2, R * 2);
        using (var wb = new LinearGradientBrush(new RectangleF(cx - R, cy - R, R * 2, R * 2), Color.FromArgb(244, 246, 250), Color.FromArgb(206, 209, 215), 90f)) g.FillEllipse(wb, cx - R, cy - R, R * 2, R * 2);
        using (var inner = new SolidBrush(Color.FromArgb(26, 28, 33))) g.FillEllipse(inner, cx - ir, cy - ir, ir * 2, ir * 2);
        using (var innerEdge = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(innerEdge, cx - ir, cy - ir, ir * 2, ir * 2);
        Color ink = Color.FromArgb(170, 70, 72, 78);
        using var f = Theme.UiFont(Math.Max(7f, R * 0.13f), FontStyle.Bold);
        using var sf = new System.Drawing.StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var ib = new SolidBrush(ink);
        g.DrawString("MENU", f, ib, new RectangleF(cx - R, cy - R * 0.78f, R * 2, R * 0.4f), sf);
        g.DrawString("⏮", f, ib, new RectangleF(cx - R * 0.95f, cy - R * 0.22f, R * 0.4f, R * 0.4f), sf);
        g.DrawString("⏭", f, ib, new RectangleF(cx + R * 0.55f, cy - R * 0.22f, R * 0.4f, R * 0.4f), sf);
        g.DrawString("⏯", f, ib, new RectangleF(cx - R * 0.2f, cy + R * 0.45f, R * 0.4f, R * 0.4f), sf);
    }

    // ---- carbon-fibre weave ----
    private static void Carbon(Graphics g, int w, int h)
    {
        using (var bg = new SolidBrush(Color.FromArgb(20, 21, 24))) g.FillRectangle(bg, 0, 0, w, h);
        int s = Math.Max(8, Math.Min(w, h) / 36);
        for (int y = 0; y < h; y += s)
            for (int x = 0; x < w; x += s)
            {
                bool even = ((x / s) + (y / s)) % 2 == 0;
                using var b = new SolidBrush(even ? Color.FromArgb(255, 32, 33, 37) : Color.FromArgb(255, 23, 24, 27));
                g.FillRectangle(b, x, y, s, s);
                using var p = new Pen(Color.FromArgb(14, 255, 255, 255));
                g.DrawLine(p, x, even ? y + s : y, x + s, even ? y : y + s);
            }
        using (var vig = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), 90f)) g.FillRectangle(vig, 0, 0, w, h);
    }
}
