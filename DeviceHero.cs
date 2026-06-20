using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The device-page "capacity hero": a rendered iPod on the left beside a segmented capacity donut
/// (Music / Video / Photos / Free) with a centred "free of total" readout and a legend. Owner-painted
/// so it renders identically on screen and via DrawToBitmap. Driven by data already in memory after load.
/// </summary>
internal sealed class DeviceHero : Control
{
    public readonly record struct Seg(string Label, long Bytes, Color Color);

    private Bitmap? _ipod;            // owned
    private Seg[] _segs = Array.Empty<Seg>();
    private long _total = 1, _free;
    private float _sweep = 1f;        // 0..1 — drives the ring fill AND the centre count-up; animated on Set()
    private Tween? _tween;
    // Cached once — Theme.UiFont/DisplayFont allocate a fresh GDI Font per call; creating them inline in
    // OnPaint would leak a handle every repaint. Disposed in Dispose().
    private readonly Font _fTotal = Theme.DisplayFont(15f, FontStyle.Bold);
    private readonly Font _fSub = Theme.UiFont(8f);
    private readonly Font _fLegend = Theme.UiFont(9f);

    public DeviceHero()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 196;
    }

    /// <summary>Set the iPod picture (TAKES OWNERSHIP), totals and segments. The LAST segment is treated as the
    /// "free/remainder" base ring; the earlier segments are drawn over it.</summary>
    public void Set(Bitmap? ipod, long total, long free, params Seg[] segs)
    {
        _tween?.Cancel();
        _ipod?.Dispose(); _ipod = ipod;
        _total = Math.Max(1, total); _free = free; _segs = segs;
        if (!Anim.MotionEnabled) { _sweep = 1f; Invalidate(); return; }
        // The donut sweeps in from 12 o'clock and the centre free-space number counts up — a little "the
        // device just told me its story" moment on every connect/refresh.
        _sweep = 0f;
        _tween = Anim.Run(720, v => { _sweep = (float)v; if (!IsDisposed) Invalidate(); },
            () => { _tween = null; _sweep = 1f; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        int h = Height;

        int cy = h / 2, r = 66, ringW = 22;
        const int legendGap = 28, legendDot = 18, legendInnerW = 150;
        int cx;
        if (_ipod is not null)
        {
            int isz = Math.Min(170, h - 10);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_ipod, new Rectangle(4, (h - isz) / 2, isz, isz));
            cx = 298;   // donut sits to the right of the iPod
        }
        else
        {
            // No iPod (the page header already shows it) → centre the [donut + legend] group in the card.
            int groupW = r * 2 + legendGap + legendDot + legendInnerW;
            cx = Math.Max(r + 8, (Width - groupW) / 2 + r);
        }
        var box = new Rectangle(cx - r, cy - r, r * 2, r * 2);
        // Base ring = the remainder (last segment); used segments overlay it from 12 o'clock — no seam math.
        Color baseCol = _segs.Length > 0 ? _segs[^1].Color : Theme.Blend(Theme.Bg, Color.White, 0.07);
        using (var bb = new SolidBrush(baseCol)) g.FillPie(bb, box, 0, 360);
        float start = -90f;
        for (int i = 0; i < _segs.Length - 1; i++)
        {
            var s = _segs[i];
            if (s.Bytes <= 0) continue;
            float sweep = (float)(s.Bytes / (double)_total * 360.0) * _sweep;
            using var sb = new SolidBrush(s.Color);
            g.FillPie(sb, box, start, sweep);
            start += sweep;
        }
        int ri = r - ringW;
        using (var hole = new SolidBrush(Parent?.BackColor ?? Theme.Bg)) g.FillEllipse(hole, cx - ri, cy - ri, ri * 2, ri * 2);

        long shownFree = _sweep >= 1f ? _free : (long)(_free * _sweep);
        TextRenderer.DrawText(g, CapacityBar.Human(shownFree), _fTotal, new Rectangle(cx - r, cy - 19, r * 2, 22), Theme.TextCol,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Loc.T("free of {0}", CapacityBar.Human(_total)), _fSub, new Rectangle(cx - r, cy + 4, r * 2, 16), Theme.Subtle,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);

        var f = _fLegend;
        int lineH = TextRenderer.MeasureText(g, "Ag", f).Height + 13;
        int lx = cx + r + legendGap, ly = cy - _segs.Length * lineH / 2;
        foreach (var s in _segs)
        {
            using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, lx, ly + (lineH - 11) / 2, 11, 11);
            TextRenderer.DrawText(g, s.Label, f, new Rectangle(lx + legendDot, ly, legendInnerW, lineH), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, CapacityBar.Human(s.Bytes), f, new Rectangle(lx + legendDot, ly, legendInnerW, lineH), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            ly += lineH;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tween?.Cancel(); _ipod?.Dispose(); _fTotal.Dispose(); _fSub.Dispose(); _fLegend.Dispose(); }
        base.Dispose(disposing);
    }
}
