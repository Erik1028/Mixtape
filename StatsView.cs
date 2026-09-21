using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The listening page: what the iPod's own counters add up to. Four headline figures, then ranked lists
/// (top artists, most played songs, the rating spread) drawn as bars, and a closing note. It reads the
/// library and nothing else - no counter is touched here, so the page is safe on any device.
/// Owner-painted like <see cref="HomeView"/>: wheel scrolling with a slim bar, and rows that lead
/// somewhere (an artist opens its page, a song plays).
/// </summary>
internal sealed class StatsView : Panel
{
    public sealed class Stat { public string Value = "", Label = ""; public bool Accent; }
    public sealed class Bar { public string Name = "", Figure = ""; public double Value; public object? Target; }
    public sealed class Section { public string Label = ""; public readonly List<Bar> Bars = new(); }

    public event Action<object>? Activated;   // a row's Target: a Track plays, an artist key opens its page

    private readonly List<Stat> _stats = new();
    private readonly List<Section> _sections = new();
    private string _note = "";
    private readonly List<(Rectangle Rect, object Target)> _hit = new();
    private object? _hover;
    private int _scroll, _contentH;
    private bool _barHover, _barDrag;
    private int _dragY0, _dragScroll0;

    private const int Pad = 22, Gap = 16, StatH = 82, RowH = 28, LabelH = 24, SectionGap = 24, BarZone = 16;
    private readonly Font _fBig = Theme.DisplayFont(21f, FontStyle.Bold);
    private readonly Font _fLabel = Theme.UiFont(Theme.SzLabel, FontStyle.Bold);
    private readonly Font _fName = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);
    private readonly Font _fFig = Theme.UiFont(Theme.SzCaption, FontStyle.Bold);
    private readonly Font _fNote = Theme.UiFont(Theme.SzCaption);

    public StatsView()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseMove += OnMove;
        MouseLeave += (_, _) => { if (_hover is not null || _barHover) { _hover = null; _barHover = false; Cursor = Cursors.Default; Invalidate(); } };
        MouseWheel += (_, e) => Glide(e.Delta);
        MouseDown += OnDown;
        MouseUp += (_, _) => { if (_barDrag) { _barDrag = false; Invalidate(); } };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.X >= Width - BarZone) return;
            if (HitTest(e.Location) is { } target) Activated?.Invoke(target);
        };
    }

    /// <summary>Everything on the page. Any part may be empty; an empty page draws the note alone.</summary>
    public void SetContent(IReadOnlyList<Stat> stats, IReadOnlyList<Section> sections, string note)
    {
        _stats.Clear(); _stats.AddRange(stats);
        _sections.Clear(); _sections.AddRange(sections);
        _note = note;
        _scroll = 0; _scrollTarget = 0; _scrollTw?.Cancel(); _scrollTw = null; _hover = null;
        BeginEnter();   // the bars fill in instead of appearing already full
        Invalidate();
    }

    public bool IsEmpty => _stats.Count == 0 && _sections.Count == 0;

    // The wheel moves a TARGET and the drawn offset chases it, so a fast spin adds up instead of restarting.
    private int _scrollTarget;
    private Tween? _scrollTw;

    private void Glide(int delta) => SetScroll(_scrollTarget - Math.Sign(delta) * Math.Max(40, Height / 6), animate: true);

    private void SetScroll(int v, bool animate = false)
    {
        int max = Math.Max(0, _contentH - Height);
        v = Math.Clamp(v, 0, max);
        _scrollTarget = v;
        if (!animate || !Anim.MotionEnabled || v == _scroll)
        {
            _scrollTw?.Cancel(); _scrollTw = null;
            if (v != _scroll) { _scroll = v; Invalidate(); }
            return;
        }
        _scrollTw?.Cancel();
        int from = _scroll;
        _scrollTw = Anim.Run(260, t => { if (IsDisposed) return; _scroll = (int)Math.Round(from + (v - from) * t); Invalidate(); },
            () => _scrollTw = null, Easings.OutCubic);
    }

    private object? HitTest(Point p)
    {
        foreach (var (r, t) in _hit) if (r.Contains(p)) return t;
        return null;
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        if (_barDrag)
        {
            int track = Math.Max(1, Height - 12);
            SetScroll(_dragScroll0 + (int)((e.Y - _dragY0) * (double)_contentH / track));
            return;
        }
        bool bh = e.X >= Width - BarZone && _contentH > Height;
        var h = HitTest(e.Location);
        if (!ReferenceEquals(h, _hover) || bh != _barHover)
        {
            _hover = h; _barHover = bh;
            Cursor = h is not null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void OnDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.X < Width - BarZone || _contentH <= Height) return;
        _barDrag = true; _dragY0 = e.Y; _dragScroll0 = _scroll;
        Invalidate();
    }


    // The page's entrance: content rises a little and fades up from the background. One tween, applied in
    // OnPaint, so it costs nothing when it is not running and needs no per-control opacity.
    private double _enter = 1;
    private Tween? _enterTw;
    /// <summary>Play the entrance. Called when the page is given its content, so it runs just behind the
    /// shell's own cross-dissolve instead of fighting it.</summary>
    private void BeginEnter()
    {
        _enterTw?.Cancel();
        if (!Anim.MotionEnabled) { _enter = 1; Invalidate(); return; }
        _enter = 0;
        _enterTw = Anim.Run(300, v => { _enter = v; if (!IsDisposed) Invalidate(); }, () => _enterTw = null, Easings.OutCubic);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _hit.Clear();

        int w = Width - BarZone, y = Pad - _scroll;
        if (IsEmpty)
        {
            TextRenderer.DrawText(g, _note, _fNote, new Rectangle(Pad, Pad, Math.Max(10, w - 2 * Pad), 40), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            _contentH = 0;
            return;
        }

        // ---- the headline figures ----
        if (_stats.Count > 0)
        {
            int cols = w >= 4 * 170 + 2 * Pad ? 4 : 2;
            int rows = (_stats.Count + cols - 1) / cols;
            int cw = (w - 2 * Pad - (cols - 1) * Gap) / cols;
            for (int i = 0; i < _stats.Count; i++)
            {
                int cx = Pad + i % cols * (cw + Gap), cy = y + i / cols * (StatH + Gap);
                // Each figure settles a beat after the one before it, left to right.
                double step = Easings.OutCubic(Math.Clamp((_enter - i * 0.07) / 0.6, 0, 1));
                DrawStat(g, new Rectangle(cx, cy + (int)Math.Round((1 - step) * 14), cw, StatH), _stats[i]);
            }
            y += rows * (StatH + Gap) - Gap + SectionGap;
        }

        // ---- the ranked sections: two columns while they fit ----
        bool twoUp = w >= 700;   // his window with the side card open is ~760 wide; one column there would be all scrolling
        int sw = twoUp ? (w - 2 * Pad - Gap) / 2 : w - 2 * Pad;
        int rowY = y, colH = 0;
        for (int i = 0; i < _sections.Count; i++)
        {
            var sec = _sections[i];
            int sx = twoUp && i % 2 == 1 ? Pad + sw + Gap : Pad;
            int sy = twoUp ? rowY : y;
            int h = DrawSection(g, new Rectangle(sx, sy, sw, 0), sec);
            if (twoUp)
            {
                colH = Math.Max(colH, h);
                if (i % 2 == 1 || i == _sections.Count - 1) { rowY += colH + SectionGap; colH = 0; }
            }
            else y += h + SectionGap;
        }
        if (twoUp) y = rowY;

        if (_note.Length > 0)
        {
            TextRenderer.DrawText(g, _note, _fNote, new Rectangle(Pad, y, Math.Max(10, w - 2 * Pad), 34), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += 34;
        }

        _contentH = y + _scroll + Pad;
        DrawScrollBar(g);
    }

    private void DrawStat(Graphics g, Rectangle r, Stat s)
    {
        using (var b = new SolidBrush(Theme.PanelBg))
        using (var p = Theme.RoundedRect(r, Theme.RadCard)) g.FillPath(b, p);
        // A figure is the point of the tile, so it SHRINKS to fit rather than being cut: "3 óra 27 perc" is
        // half again as long as "3 h 27 m", and an ellipsised number says nothing at all.
        int box = r.Width - 32;
        Font f = _fBig; Font? fit = null;
        int wNeed = TextRenderer.MeasureText(s.Value, f, new Size(int.MaxValue, 40), TextFormatFlags.NoPrefix).Width;
        if (wNeed > box)
        {
            fit = Theme.DisplayFont(Math.Max(12f, 21f * box / wNeed), FontStyle.Bold);
            f = fit;
        }
        TextRenderer.DrawText(g, s.Value, f, new Rectangle(r.X + 16, r.Y + 11, box, 36), s.Accent ? Theme.AccentBright : Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        fit?.Dispose();
        TextRenderer.DrawText(g, s.Label.ToUpperInvariant(), _fLabel, new Rectangle(r.X + 16, r.Bottom - 30, r.Width - 32, 20), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Draws one ranked list and returns its height (the caller decides only x and width).</summary>
    private int DrawSection(Graphics g, Rectangle r, Section sec)
    {
        // Each bar draws itself to its share of the entrance, a little later than the one above it.
        double grow = _enter;
        int h = LabelH + sec.Bars.Count * RowH + 14;
        var card = new Rectangle(r.X, r.Y, r.Width, h);
        using (var b = new SolidBrush(Theme.PanelBg))
        using (var p = Theme.RoundedRect(card, Theme.RadCard)) g.FillPath(b, p);

        TextRenderer.DrawText(g, sec.Label.ToUpperInvariant(), _fLabel, new Rectangle(card.X + 16, card.Y + 8, card.Width - 32, LabelH - 6), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        double max = 1;
        foreach (var b in sec.Bars) max = Math.Max(max, b.Value);
        // The name is the subject of the row, so it gets the space; the bar is a hint at the shape of the
        // numbers, not a race track, and it is capped so it cannot swallow the card.
        int figW = 40;
        int nameW = Math.Clamp((int)(card.Width * 0.44), 90, 260);
        int barX = card.X + 16 + nameW + 14;
        int barW = Math.Clamp(card.Right - 16 - figW - 10 - barX, 20, 220);

        for (int i = 0; i < sec.Bars.Count; i++)
        {
            var b = sec.Bars[i];
            var row = new Rectangle(card.X + 6, card.Y + LabelH + i * RowH, card.Width - 12, RowH);
            bool hot = b.Target is not null && ReferenceEquals(_hover, b.Target);
            if (hot)
            {
                using var hb = new SolidBrush(Theme.RowHover);
                using var hp = Theme.RoundedRect(row, Theme.RadControl);
                g.FillPath(hb, hp);
            }
            if (b.Target is not null) _hit.Add((row, b.Target));

            TextRenderer.DrawText(g, b.Name, _fName, new Rectangle(card.X + 16, row.Y, nameW, RowH), hot ? Theme.TextCol : Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            double step = Math.Clamp((grow - i * 0.045) / 0.55, 0, 1);
            int bw = (int)Math.Round(barW * Math.Clamp(b.Value / max, 0, 1) * Easings.OutCubic(step));
            var track = new Rectangle(barX, row.Y + RowH / 2 - 4, barW, 8);
            using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.35)))
            using (var tp = Theme.RoundedRect(track, 4)) g.FillPath(tb, tp);
            if (bw >= 4)
            {
                using var fb = new SolidBrush(hot ? Theme.AccentBright : Theme.Accent);
                using var fp = Theme.RoundedRect(new Rectangle(track.X, track.Y, bw, track.Height), 4);
                g.FillPath(fb, fp);
            }
            TextRenderer.DrawText(g, b.Figure, _fFig, new Rectangle(card.Right - 16 - figW, row.Y, figW, RowH), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        return h;
    }

    private void DrawScrollBar(Graphics g)
    {
        if (_contentH <= Height) return;
        int track = Height - 12;
        int th = Math.Max(30, (int)(track * (double)Height / _contentH));
        int ty = 6 + (int)((track - th) * (_scroll / (double)Math.Max(1, _contentH - Height)));
        using var b = new SolidBrush(Color.FromArgb(_barHover || _barDrag ? 90 : 46, 255, 255, 255));
        using var p = Theme.RoundedRect(new Rectangle(Width - 10, ty, 5, th), 2.5f);
        g.FillPath(b, p);
    }

    /// <summary>The page's header art: the rail's bar-chart mark on the same rounded accent tile every other
    /// header wears.</summary>
    public static Bitmap HeaderTile(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float rad = Math.Max(3, size * Theme.TileFrac);
        using (var tile = Theme.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), rad))
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Theme.Blend(Theme.Accent, Color.White, 0.10), Theme.Blend(Theme.Accent, Color.Black, 0.30), Theme.ArtAngle))
            g.FillPath(bg, tile);
        float s = size;
        using var ink = new SolidBrush(Color.FromArgb(235, Theme.OnAccent));
        float bw = s * 0.15f, bx = s * 0.24f, baseY = s * 0.76f;
        float[] tops = { s * 0.54f, s * 0.38f, s * 0.24f };
        for (int i = 0; i < 3; i++)
            using (var bp = Theme.RoundedRect(new RectangleF(bx + i * (bw + s * 0.08f), tops[i], bw, baseY - tops[i]), bw / 2.4f))
                g.FillPath(ink, bp);
        using (var ip = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), rad))
        using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255))) g.DrawPath(pen, ip);
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fBig.Dispose(); _fLabel.Dispose(); _fName.Dispose(); _fFig.Dispose(); _fNote.Dispose(); }
        base.Dispose(disposing);
    }
}
