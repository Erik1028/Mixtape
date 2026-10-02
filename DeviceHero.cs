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
    private readonly Font _fSub = Theme.UiFont(8f);
    private readonly Font _fLegend = Theme.UiFont(Theme.SzBody);
    private int _ringR = 0;          // the donut radius the centre font is sized for; rebuild the font only when it changes
    private Font? _fTotalDyn;        // centre "free" number — scales with the (width-driven) ring radius

    /// <summary>The centre of the ring (or the pie) held down for two seconds: the click wheel's centre button
    /// in About, which is where the first iPods kept Brick. See Easter.cs.</summary>
    public event Action? CentreHeld;
    private Rectangle _hub;
    private readonly System.Windows.Forms.Timer _hold = new() { Interval = 2000 };

    /// <summary>Classic: the line under the pie ("Drive E"), as the 95 drive sheet put "Drive C" under its own.</summary>
    public string? Caption { get; set; }

    // Classic: the sixteen colours, each slice's top in the bright one and its side wall in the darker twin. Music
    // takes 95's own "used space" blue and Free its magenta, so a music iPod reads like every drive sheet of 1995.
    private static readonly (Color Top, Color Side)[] ClassicSlices =
    {
        (Color.FromArgb(0, 0, 255), Color.FromArgb(0, 0, 128)),        // music: blue on navy
        (Color.FromArgb(0, 128, 128), Color.FromArgb(0, 64, 64)),      // video: teal
        (Color.FromArgb(128, 128, 0), Color.FromArgb(64, 64, 0)),      // photos: olive
        (Color.FromArgb(128, 128, 128), Color.FromArgb(64, 64, 64)),   // other: grey
    };
    private static readonly (Color Top, Color Side) ClassicFree = (Color.FromArgb(255, 0, 255), Color.FromArgb(128, 0, 128));   // free: magenta on purple

    public DeviceHero()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 196;
        _hold.Tick += (_, _) => { _hold.Stop(); CentreHeld?.Invoke(); };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _hub.Contains(e.Location)) _hold.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _hold.Stop(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hold.Stop(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_hold.Enabled && !_hub.Contains(e.Location)) _hold.Stop(); }

    /// <summary>Set the iPod picture (TAKES OWNERSHIP), totals and segments. The LAST segment is treated as the
    /// "free/remainder" base ring; the earlier segments are drawn over it.</summary>
    public void Set(Bitmap? ipod, long total, long free, params Seg[] segs)
    {
        _tween?.Cancel();
        _ipod?.Dispose(); _ipod = ipod;
        _total = Math.Max(1, total); _free = free; _segs = segs;
        if (!Anim.MotionEnabled || Theme.Classic) { _sweep = 1f; Invalidate(); return; }   // 1995 drew its pie in one go
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
        if (Theme.Classic) { PaintClassic(g); return; }
        int h = Height;

        // The donut grows with the card so it doesn't look lost on a wide device page (was pinned at r=66). Capped at 90
        // so 2r=180 still fits the 196px height (no layout reflow). The centre number scales with it (font cached by r).
        int r = Math.Clamp(66 + (Width - 480) / 14, 66, 90);
        int ringW = (int)Math.Round(r * 0.32f);
        if (r != _ringR || _fTotalDyn is null) { _fTotalDyn?.Dispose(); _fTotalDyn = Theme.DisplayFont(15f * r / 66f, FontStyle.Bold); _ringR = r; }
        int cy = h / 2;
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
        _hub = new Rectangle(cx - ri, cy - ri, ri * 2, ri * 2);
        using (var hole = new SolidBrush(Parent?.BackColor ?? Theme.Bg)) g.FillEllipse(hole, cx - ri, cy - ri, ri * 2, ri * 2);

        long shownFree = _sweep >= 1f ? _free : (long)(_free * _sweep);
        string freeTxt = CapacityBar.Human(shownFree);
        var fsz = TextRenderer.MeasureText(g, freeTxt, _fTotalDyn!);
        TextRenderer.DrawText(g, freeTxt, _fTotalDyn!, new Rectangle(cx - r, cy - 4 - fsz.Height, r * 2, fsz.Height), Theme.TextCol,
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

    private (Color Top, Color Side) ClassicColour(int i) =>
        i == _segs.Length - 1 ? ClassicFree : ClassicSlices[Math.Min(i, ClassicSlices.Length - 1)];

    /// <summary>
    /// Classic: the General page of a 95 drive's Properties - a legend of little framed swatches with the exact byte
    /// count and the short size, a rule, the capacity; and beside it the tilted 3D pie, drawn the way GDI drew it
    /// (Pie() with no anti-aliasing), its side wall a stack of the same slices in the darker colours.
    /// </summary>
    private void PaintClassic(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        var f = _fLegend;
        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        string Bytes(long b) => Loc.T("{0} bytes", b.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        string capLabel = Loc.T("Capacity:");
        int lineH = TextRenderer.MeasureText(g, "Ag", f).Height + 7;
        int labelW = TextRenderer.MeasureText(g, capLabel, f).Width, bytesW = TextRenderer.MeasureText(g, Bytes(_total), f).Width, humanW = TextRenderer.MeasureText(g, CapacityBar.Human(_total), f).Width;
        foreach (var s in _segs)
        {
            labelW = Math.Max(labelW, TextRenderer.MeasureText(g, s.Label + ":", f).Width);
            humanW = Math.Max(humanW, TextRenderer.MeasureText(g, CapacityBar.Human(s.Bytes), f).Width);
        }
        const int x0 = 16, sw = 10;
        int labelX = x0 + sw + 8, bytesR = labelX + labelW + 18 + bytesW, humanR = bytesR + 18 + humanW;
        int ly = Math.Max(4, (Height - ((_segs.Length + 1) * lineH + 9)) / 2);
        using (var edge = new Pen(Theme.FaceDark))
            for (int i = 0; i < _segs.Length; i++)
            {
                var s = _segs[i];
                var box = new Rectangle(x0, ly + (lineH - sw) / 2, sw, sw);
                using (var b = new SolidBrush(ClassicColour(i).Top)) g.FillRectangle(b, box);
                g.DrawRectangle(edge, box.X, box.Y, box.Width - 1, box.Height - 1);   // the swatch's black frame
                TextRenderer.DrawText(g, s.Label + ":", f, new Rectangle(labelX, ly, labelW + 8, lineH), Theme.TextCol, L);
                TextRenderer.DrawText(g, Bytes(s.Bytes), f, new Rectangle(bytesR - bytesW - 8, ly, bytesW + 8, lineH), Theme.TextCol, R);
                TextRenderer.DrawText(g, CapacityBar.Human(s.Bytes), f, new Rectangle(humanR - humanW - 8, ly, humanW + 8, lineH), Theme.TextCol, R);
                ly += lineH;
            }
        ly += 4;
        using (var sh = new Pen(Theme.FaceShadow)) g.DrawLine(sh, x0, ly, humanR, ly);   // the etched rule over the capacity
        using (var hi = new Pen(Theme.FaceHi)) g.DrawLine(hi, x0, ly + 1, humanR, ly + 1);
        ly += 5;
        TextRenderer.DrawText(g, capLabel, f, new Rectangle(labelX, ly, labelW + 8, lineH), Theme.TextCol, L);
        TextRenderer.DrawText(g, Bytes(_total), f, new Rectangle(bytesR - bytesW - 8, ly, bytesW + 8, lineH), Theme.TextCol, R);
        TextRenderer.DrawText(g, CapacityBar.Human(_total), f, new Rectangle(humanR - humanW - 8, ly, humanW + 8, lineH), Theme.TextCol, R);

        // the pie, centred in the room right of the legend
        int left = humanR + 36, room = Math.Max(0, Width - left - 16);
        int pw = Math.Min(room, 250);
        if (pw < 90) return;   // too narrow for a pie: the legend alone
        int ph = (int)Math.Round(pw * 0.42f), depth = Math.Max(8, pw / 14);
        bool cap = !string.IsNullOrEmpty(Caption);
        int total = ph + depth + (cap ? lineH + 4 : 0);
        int px = left + (room - pw) / 2, py = Math.Max(4, (Height - total) / 2);
        _hub = new Rectangle(px + pw / 4, py, pw / 2, ph);
        for (int d = depth; d >= 0; d--)   // the side wall from the bottom up, then the top face over it
        {
            var box = new Rectangle(px, py + d, pw, ph);
            bool side = d > 0;
            // every slice a pie, the free space too (the rest of the turn): under a full ellipse its rim poked a pixel
            // past the pies' own here and there, and streaked the side wall with the free colour
            float start = -90f;
            for (int i = 0; i < _segs.Length; i++)
            {
                bool last = i == _segs.Length - 1;
                float sweepDeg = last ? 270f - start : (float)(_segs[i].Bytes / (double)_total * 360.0);
                if (sweepDeg <= 0f) continue;
                using var sb = new SolidBrush(side ? ClassicColour(i).Side : ClassicColour(i).Top);
                g.FillPie(sb, box, start, sweepDeg);
                start += sweepDeg;
            }
        }
        if (cap)
            TextRenderer.DrawText(g, Caption, f, new Rectangle(px - 20, py + ph + depth + 4, pw + 40, lineH), Theme.TextCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hold.Dispose(); _tween?.Cancel(); _ipod?.Dispose(); _fTotalDyn?.Dispose(); _fSub.Dispose(); _fLegend.Dispose(); }
        base.Dispose(disposing);
    }
}
