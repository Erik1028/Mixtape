using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The side panel: a third card, docked to the right of the content when the window is wide enough, holding what
/// used to be popovers only — Up Next (the queue, drag to reorder), History (what played, newest first) and the
/// lyrics — under a tab strip. The card owns the strip and the foot (its four carved corners); the active tab's
/// control fills the middle. Narrow windows keep the popovers; the host decides.
/// </summary>
internal sealed class SidePanel : Panel
{
    public enum Tab { UpNext, History, Lyrics }

    public readonly UpNextPanel UpNext = new() { Docked = true, Visible = false };
    public readonly HistoryPanel History = new() { Visible = false };
    public readonly LyricsPanel Lyrics = new() { Docked = true, Visible = false };

    public event Action? CloseRequested;
    public event Action<Tab>? TabChanged;
    public Tab Current { get; private set; } = Tab.UpNext;

    public const int W = 320;             // the card's width
    private static int StripH => Theme.Classic ? 28 : 44;   // Classic: just the property-sheet tabs over their page
    private const int Pad = 14;
    private int _hoverTab = -1;
    private bool _hoverClose;
    private readonly Font _fTab = Theme.UiFont(Theme.SzBody, FontStyle.Bold);
    private readonly Font _fTab95 = Theme.UiFont(Theme.SzBody);   // Classic: a property sheet's tabs are in the plain face
    private readonly List<(Rectangle Rect, Tab Tab)> _tabHit = new();

    public SidePanel()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(UpNext); Controls.Add(History); Controls.Add(Lyrics);
        MouseMove += (_, e) => UpdateHover(e.Location);
        MouseLeave += (_, _) => { if (_hoverTab != -1 || _hoverClose) { _hoverTab = -1; _hoverClose = false; Invalidate(); } };
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            foreach (var (rect, tab) in _tabHit) if (rect.Contains(e.Location)) { Select(tab); return; }
        };
        Select(Tab.UpNext);
    }

    private Rectangle CloseRect => Rectangle.Empty;   // no x of its own: the deck's queue button (pressed while open) toggles the card

    public void Select(Tab t)
    {
        Current = t;
        UpNext.Visible = t == Tab.UpNext;
        History.Visible = t == Tab.History;
        Lyrics.Visible = t == Tab.Lyrics;
        LayoutBody();
        Invalidate();
        TabChanged?.Invoke(t);
    }

    public static Tab ParseTab(string? s) => s switch { "History" => Tab.History, "Lyrics" => Tab.Lyrics, _ => Tab.UpNext };

    private void LayoutBody()
    {
        // All three, visible or not: the Visible getter is false while the WINDOW is still hidden (startup with the card
        // remembered open), and a tab laid out only "when visible" would keep its 200x44 default until the next resize.
        var body = new Rectangle(0, StripH, Width, Math.Max(0, Height - StripH - CardFoot.H));
        if (Theme.Classic) body = Rectangle.Inflate(ClassicPage, -4, -4);   // inside the property page's raised edge
        foreach (Control c in Controls) c.Bounds = body;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBody(); }

    private void UpdateHover(Point p)
    {
        int ht = -1;
        for (int i = 0; i < _tabHit.Count; i++) if (_tabHit[i].Rect.Contains(p)) ht = i;
        bool hc = CloseRect.Contains(p);
        if (ht != _hoverTab || hc != _hoverClose) { _hoverTab = ht; _hoverClose = hc; Cursor = ht >= 0 || hc ? Theme.HandCursor : Cursors.Default; Invalidate(); }
    }

    /// <summary>Classic: the property page the three tabs belong to.</summary>
    private Rectangle ClassicPage => Rectangle.FromLTRB(2, StripH, Math.Max(3, Width - 2), Math.Max(StripH + 1, Height - CardFoot.H - 2));

    private static (Tab tab, string name)[] Names() => new[] { (Tab.UpNext, Loc.T("Up Next")), (Tab.History, Loc.T("History")), (Tab.Lyrics, Loc.T("Lyrics")) };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Theme.Classic)
        {
            // a 1995 property sheet: the tabs on the window face, the chosen one standing forward and joined to its page
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Theme.Face);
            _tabHit.Clear();
            var page = ClassicPage;
            int tx = page.X + 2;
            var cells = new List<(Rectangle r, string name, bool on)>();
            foreach (var (tab, name) in Names())
            {
                int w = TextRenderer.MeasureText(name, _fTab95).Width + 12;
                var r = new Rectangle(tx, page.Y - ClassicPropertySheet.TabH + 2, w, ClassicPropertySheet.TabH - 2);
                _tabHit.Add((r, tab));
                cells.Add((r, name, tab == Current));
                tx += w;
            }
            Theme.Bevel(g, page, raised: true);
            foreach (var c in cells) if (!c.on) ClassicPropertySheet.DrawTab(g, c.r, c.name, false, _fTab95);
            foreach (var c in cells) if (c.on) ClassicPropertySheet.DrawTab(g, new Rectangle(c.r.X - 2, c.r.Y - 2, c.r.Width + 4, c.r.Height + 3), c.name, true, _fTab95);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        // the tab strip: the active tab on an accent-tinted chip, the others quiet
        _tabHit.Clear();
        int x = Pad;
        foreach (var (tab, name) in Names())
        {
            int tw = TextRenderer.MeasureText(name, _fTab, new Size(int.MaxValue, 24), TextFormatFlags.NoPrefix).Width + 24;
            var r = new Rectangle(x, (StripH - 26) / 2, tw, 26);
            _tabHit.Add((r, tab));
            bool on = tab == Current, hover = _tabHit.Count - 1 == _hoverTab;
            if (on) { using var b = new SolidBrush(Theme.Blend(Theme.Bg, Theme.Accent, 0.16)); using var p = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(b, p); }
            else if (hover && Theme.ShowHover) { using var b = new SolidBrush(Theme.RowHover); using var p = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(b, p); }
            TextRenderer.DrawText(g, name, _fTab, r, on ? Theme.AccentBright : hover ? Theme.TextCol : Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += tw + 6;
        }
        using (var pen = new Pen(Theme.HairLine)) g.DrawLine(pen, Pad, StripH - 1, Width - Pad, StripH - 1);

        Theme.CarveCardCorners(g, this, Theme.RadShell, true, true, true, true);   // a floating card like the rail: all four corners
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fTab.Dispose(); _fTab95.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>What played, newest first — the same rows as the queue (cover, title, artist) with when it played on the
/// right. Click a row to play that song again. A pure view: the host keeps the list.</summary>
internal sealed class HistoryPanel : Control
{
    public event Action<Track>? ActivateRequested;

    private readonly List<(Track t, Bitmap? art, DateTime at)> _items = new();
    private int _scrollY, _hoverRow = -1;
    private bool _thumbDrag; private int _thumbGrabY, _thumbGrabScroll;
    private const int RowH = 46, Pad = 14, Art = 34, ThumbW = 6, Top = 6;
    private readonly Font _fTitle = Theme.UiFont(9.5f, FontStyle.Bold), _fArtist = Theme.UiFont(8.5f), _fWhen = Theme.UiFont(8f), _fEmpty = Theme.UiFont(9.5f);

    public HistoryPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    public void SetData(IReadOnlyList<(Track t, Bitmap? art, DateTime at)> items)
    {
        _items.Clear(); _items.AddRange(items);
        _scrollY = Math.Clamp(_scrollY, 0, MaxScroll);
        Invalidate();
    }

    private int ContentH => _items.Count * RowH + Top * 2;
    private int MaxScroll => Math.Max(0, ContentH - Height);
    private int RowTop(int i) => Top - _scrollY + i * RowH;
    private int RowAt(int y) { int i = (y - Top + _scrollY) / RowH; return y >= Top && i >= 0 && i < _items.Count ? i : -1; }
    private (int y, int h) Thumb()
    {
        int track = Height - 8;
        int h = Math.Max(28, (int)(track * (double)Height / Math.Max(1, ContentH)));
        int y = 4 + (MaxScroll == 0 ? 0 : (int)((track - h) * (double)_scrollY / MaxScroll));
        return (y, h);
    }

    private static string When(DateTime at)
    {
        var today = DateTime.Today;
        if (at.Date == today) return at.ToString("HH:mm");
        if (at.Date == today.AddDays(-1)) return Loc.T("Yesterday");
        return Loc.Lang == "hu" ? at.ToString("MMM d.", new System.Globalization.CultureInfo("hu-HU")) : at.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = Theme.TextHint;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, Loc.T("Nothing played yet."), _fEmpty, new Rectangle(Pad, Top + 8, Width - 2 * Pad, 60), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            return;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            int top = RowTop(i);
            if (top + RowH < 0 || top > Height) continue;
            bool hovered = i == _hoverRow;
            if (hovered && Theme.ShowHover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(new Rectangle(6, top + 3, Width - 12, RowH - 6), 6); g.FillPath(hb, hp); }
            var (t, art, at) = _items[i];
            var ar = new Rectangle(Pad, top + (RowH - Art) / 2, Art, Art);
            using (var clip = Theme.RoundedRect(new RectangleF(ar.X, ar.Y, ar.Width, ar.Height), Math.Max(3, ar.Width * Theme.TileFrac)))
            {
                using var saved = g.Clip; g.SetClip(clip, CombineMode.Intersect);
                if (art is not null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(art, ar); }
                else using (var ph = new LinearGradientBrush(ar, Theme.Blend(Theme.Bg, Color.White, 0.09), Theme.Blend(Theme.Bg, Color.Black, 0.10), 60f)) g.FillRectangle(ph, ar);
                g.Clip = saved;
            }
            string when = When(at);
            int ww = TextRenderer.MeasureText(when, _fWhen, new Size(120, RowH), TextFormatFlags.NoPrefix).Width;
            int tx = Pad + Art + 11, tw = Width - tx - Pad - ww - 8;
            TextRenderer.DrawText(g, t.DisplayTitle, _fTitle, new Rectangle(tx, top + 7, tw, 17), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, t.Artist ?? "", _fArtist, new Rectangle(tx, top + 24, tw, 15), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, when, _fWhen, new Rectangle(Width - Pad - ww, top + 8, ww, 16), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }
        if (MaxScroll > 0)
        {
            var (ty, th) = Thumb();
            using var b = new SolidBrush(Theme.Blend(Theme.Bg, Theme.TextCol, _thumbDrag ? 0.40 : 0.22));
            using var p = Theme.RoundedRect(new RectangleF(Width - ThumbW - 3, ty, ThumbW, th), ThumbW / 2f);
            g.FillPath(b, p);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (MaxScroll <= 0) return;
        _scrollY = Math.Clamp(_scrollY - Math.Sign(e.Delta) * RowH, 0, MaxScroll);
        UpdateHover(PointToClient(Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (MaxScroll > 0) { var (ty, th) = Thumb(); if (e.X >= Width - ThumbW - 6 && e.Y >= ty && e.Y <= ty + th) { _thumbDrag = true; _thumbGrabY = e.Y; _thumbGrabScroll = _scrollY; return; } }
        int row = RowAt(e.Y);
        if (row >= 0) ActivateRequested?.Invoke(_items[row].t);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_thumbDrag)
        {
            int track = Height - 8; var (_, th) = Thumb();
            double per = MaxScroll / (double)Math.Max(1, track - th);
            _scrollY = Math.Clamp(_thumbGrabScroll + (int)((e.Y - _thumbGrabY) * per), 0, MaxScroll);
            Invalidate(); return;
        }
        UpdateHover(e.Location);
    }

    private void UpdateHover(Point p)
    {
        int row = RowAt(p.Y);
        if (row != _hoverRow) { _hoverRow = row; Cursor = row >= 0 ? Theme.HandCursor : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (_thumbDrag) { _thumbDrag = false; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hoverRow != -1) { _hoverRow = -1; Invalidate(); } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fTitle.Dispose(); _fArtist.Dispose(); _fWhen.Dispose(); _fEmpty.Dispose(); }
        base.Dispose(disposing);
    }
}
