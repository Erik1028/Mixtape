using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The iTunes-style storage bar: a single rounded track filled with proportional coloured
/// segments (Audio / Video / Photos / Other) over a dark "free space" remainder, with a small
/// legend underneath. Owner-painted so it renders identically on screen and via DrawToBitmap.
/// </summary>
internal sealed class CapacityBar : Control
{
    public readonly record struct Seg(string Label, long Bytes, Color Color);

    private Seg[] _segs = Array.Empty<Seg>();
    private long _total;

    public CapacityBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 76; // bar + up to two wrapped legend rows
    }

    public void Set(long total, params Seg[] segs)
    {
        _total = Math.Max(1, total);
        _segs = segs;
        Height = MeasureHeight(); // self-size so a DPI-wrapped second legend row never clips or overlaps siblings
        Invalidate();
    }

    /// <summary>Height for the bar plus its (possibly wrapped) legend at the current width and DPI.</summary>
    private int MeasureHeight()
    {
        const int barH = 16;
        using var f = Theme.UiFont(8.5f);
        int lineH = TextRenderer.MeasureText("Ag", f).Height;
        int lx = 0, ly = barH + 12;
        foreach (var s in _segs)
        {
            if (s.Bytes <= 0) continue;
            int entryW = 13 + TextRenderer.MeasureText($"{s.Label}  {Human(s.Bytes)}", f).Width + 18;
            if (Width > 0 && lx > 0 && lx + entryW > Width) { lx = 0; ly += lineH + 5; }
            lx += entryW;
        }
        return ly + lineH + 4;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        const int barH = 16;
        var bar = new RectangleF(0, 0, Width - 1, barH);
        using (var track = Theme.RoundedRect(bar, barH / 2f))
        {
            using (var tb = new SolidBrush(Theme.Blend(Theme.Bg, Color.White, 0.07))) g.FillPath(tb, track); // visible recessed groove
            // Clip to the rounded track so segments inherit the rounded ends.
            var saved = g.Clip;
            g.SetClip(track, CombineMode.Intersect);
            float xExact = 0;
            foreach (var s in _segs)
            {
                if (s.Bytes <= 0) continue;
                float w = (float)((double)s.Bytes / _total * (Width - 1));
                // Integer-snapped boundaries so adjacent segments share an exact edge (no sub-pixel seam).
                float xi = (float)Math.Floor(xExact);
                float wi = (float)(Math.Ceiling(xExact + w) - Math.Floor(xExact));
                using var b = new LinearGradientBrush(new RectangleF(xi, 0, wi, barH),
                    Theme.Blend(s.Color, Color.White, 0.10), Theme.Blend(s.Color, Color.Black, 0.08), 90f);
                g.FillRectangle(b, xi, 0, wi, barH);
                xExact += w;
            }
            g.Clip = saved;
        }

        // Legend: coloured dot + "Label 1.2 GB", laid out left to right, wrapping to a second row.
        // Line height is measured from the font so it tracks DPI scaling (no clipped descenders at 125/150%).
        using var f = Theme.UiFont(8.5f);
        int lineH = TextRenderer.MeasureText(g, "Ag", f).Height;
        int lx = 0, ly = barH + 12;
        foreach (var s in _segs)
        {
            if (s.Bytes <= 0) continue;
            string text = $"{s.Label}  {Human(s.Bytes)}";
            var sz = TextRenderer.MeasureText(g, text, f);
            int entryW = 13 + sz.Width + 18;
            if (lx > 0 && lx + entryW > Width) { lx = 0; ly += lineH + 5; } // wrap
            using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, lx, ly + (lineH - 9) / 2, 9, 9);
            TextRenderer.DrawText(g, text, f, new Rectangle(lx + 13, ly, sz.Width, lineH), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.Top);
            lx += entryW;
        }
    }

    public static string Human(long bytes)
    {
        if (bytes >= 1_000_000_000L) return $"{bytes / 1e9:0.0} GB";
        if (bytes >= 1_000_000L) return $"{bytes / 1e6:0.0} MB";
        if (bytes >= 1000L) return $"{bytes / 1e3:0.0} KB";
        return $"{bytes} B";
    }
}
