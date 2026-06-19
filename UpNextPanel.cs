using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The "Up Next" overlay: a right-side panel listing the current track (pinned) and the queued upcoming
/// tracks. Owner-drawn (no child controls); it manages its own scroll, hover, drag-to-reorder, and a remove
/// (×) per row. It's a pure view — all mutations go out as events that <see cref="MainForm"/> applies to the
/// shared <c>PlayQueue</c>, which then calls <see cref="SetData"/> to refresh.
/// </summary>
internal sealed class UpNextPanel : Control
{
    public event Action? CloseRequested;
    public event Action? ClearRequested;
    public event Action<Track>? RemoveRequested;
    public event Action<Track>? ActivateRequested;   // double-click an upcoming item → jump to it
    public event Action<int, int>? MoveRequested;     // reorder: from → to (indices within the upcoming list)

    private Track? _now;
    private Bitmap? _nowArt;
    private readonly List<(Track t, Bitmap? art)> _items = new();
    private int _scrollY;
    private string? _hint;   // e.g. "Repeat One is on"

    private int _hoverRow = -1;
    private bool _hoverClose, _hoverClear, _hoverRemove;
    private bool _dragging; private int _dragIndex = -1, _dropIndex = -1, _dragStartY;
    private bool _thumbDrag; private int _thumbGrabY, _thumbGrabScroll;

    private const int HeaderH = 44, NowH = 54, RowH = 46, Pad = 14, Art = 34, ThumbW = 6;

    public UpNextPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBg;
    }

    public void SetData(Track? now, Bitmap? nowArt, IReadOnlyList<(Track t, Bitmap? art)> items, string? hint)
    {
        _now = now; _nowArt = nowArt; _hint = hint;
        _items.Clear(); _items.AddRange(items);
        ClampScroll();
        Invalidate();
    }

    /// <summary>The content height (header + pinned now-playing row + every queued row) — used to size the
    /// floating flyout to fit, before it falls back to scrolling.</summary>
    public static int DesiredHeight(int itemCount, bool hasNow, bool hasHint)
        => HeaderH + (hasNow ? NowH : 0) + itemCount * RowH + 8 + (hasHint ? 26 : 0);

    // ---- geometry ----
    private int ListTop => HeaderH + (_now is not null ? NowH : 0);
    private int ListViewH => Math.Max(0, Height - ListTop);
    private int ContentH => _items.Count * RowH + 6;
    private int MaxScroll => Math.Max(0, ContentH - ListViewH);
    private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0, MaxScroll);
    private Rectangle CloseRect => new(Width - Pad - 22, (HeaderH - 22) / 2, 22, 22);
    private Rectangle ClearRect => new(CloseRect.Left - 8 - 50, (HeaderH - 22) / 2, 50, 22);
    private int RowTop(int i) => ListTop - _scrollY + i * RowH;
    private int RowAt(int y)
    {
        if (y < ListTop || y >= Height) return -1;
        int i = (y - ListTop + _scrollY) / RowH;
        return i >= 0 && i < _items.Count ? i : -1;
    }
    private Rectangle RemoveRect(int rowTop) => new(Width - Pad - 22, rowTop + (RowH - 22) / 2, 22, 22);
    private (int y, int h) Thumb()
    {
        int track = ListViewH - 8;
        int h = Math.Max(28, (int)(track * (double)ListViewH / Math.Max(1, ContentH)));
        int y = ListTop + 4 + (MaxScroll == 0 ? 0 : (int)((track - h) * (double)_scrollY / MaxScroll));
        return (y, h);
    }

    // ---- paint ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.PanelBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // header
        TextRenderer.DrawText(g, "Up Next", Theme.DisplayFont(12.5f, FontStyle.Bold), new Rectangle(Pad, 0, Width - 2 * Pad - 80, HeaderH),
            Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (_items.Count > 0)
        {
            var ts = TextRenderer.MeasureText(g, "Up Next", Theme.DisplayFont(12.5f, FontStyle.Bold));
            TextRenderer.DrawText(g, _items.Count.ToString(), Theme.UiFont(9f, FontStyle.Bold), new Rectangle(Pad + ts.Width + 7, 0, 40, HeaderH),
                Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
        if (_items.Count > 0)
        {
            if (_hoverClear) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(ClearRect, Theme.RadControl); g.FillPath(hb, hp); }
            TextRenderer.DrawText(g, "Clear", Theme.UiFont(9f), ClearRect, _hoverClear ? Theme.TextCol : Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        DrawX(g, CloseRect, _hoverClose);
        using (var pen = new Pen(Theme.HairLine)) g.DrawLine(pen, Pad, HeaderH - 1, Width - Pad, HeaderH - 1);

        // now-playing row (pinned)
        if (_now is not null)
        {
            var nr = new Rectangle(0, HeaderH, Width, NowH);
            using (var tint = new SolidBrush(Theme.Blend(Theme.PanelBg, Theme.Accent, 0.10))) g.FillRectangle(tint, nr);
            int cy = HeaderH + (NowH - Art) / 2;
            DrawArt(g, _nowArt, new Rectangle(Pad, cy, Art, Art));
            int tx = Pad + Art + 11, tw = Width - tx - Pad;
            TextRenderer.DrawText(g, "NOW PLAYING", Theme.UiFont(7f, FontStyle.Bold), new Rectangle(tx, HeaderH + 8, tw, 12), Theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _now.DisplayTitle, Theme.UiFont(9.75f, FontStyle.Bold), new Rectangle(tx, HeaderH + 20, tw, 18), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _now.Artist ?? "", Theme.UiFont(8.5f), new Rectangle(tx, HeaderH + 37, tw, 15), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var pen = new Pen(Theme.HairLine); g.DrawLine(pen, Pad, HeaderH + NowH - 1, Width - Pad, HeaderH + NowH - 1);
        }

        // upcoming list (clipped)
        var clip = new Rectangle(0, ListTop, Width, ListViewH);
        var saved = g.Clip; g.SetClip(clip, CombineMode.Intersect);
        if (_items.Count == 0)
        {
            string msg = "Nothing queued.\nRight-click songs → “Add to queue”.";
            TextRenderer.DrawText(g, msg, Theme.UiFont(9.5f), new Rectangle(Pad, ListTop + 8, Width - 2 * Pad, 60), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }
        for (int i = 0; i < _items.Count; i++)
        {
            int top = RowTop(i);
            if (top + RowH < ListTop || top > Height) continue;   // off-screen
            bool hovered = i == _hoverRow && !_dragging;
            if (hovered) { using var hb = new SolidBrush(Theme.RowHover); g.FillRectangle(hb, new Rectangle(6, top + 3, Width - 12, RowH - 6)); }
            int cy = top + (RowH - Art) / 2;
            DrawArt(g, _items[i].art, new Rectangle(Pad, cy, Art, Art));
            int tx = Pad + Art + 11, tw = Width - tx - Pad - (hovered ? 24 : 0);
            var t = _items[i].t;
            TextRenderer.DrawText(g, t.DisplayTitle, Theme.UiFont(9.5f, FontStyle.Bold), new Rectangle(tx, top + 7, tw, 17), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, t.Artist ?? "", Theme.UiFont(8.5f), new Rectangle(tx, top + 24, tw, 15), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (hovered) DrawX(g, RemoveRect(top), _hoverRemove);
        }
        // drag insertion line
        if (_dragging && _dropIndex >= 0)
        {
            int y = Math.Clamp(RowTop(_dropIndex), ListTop, Height - 2);
            using var pen = new Pen(Theme.Accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, Pad, y, Width - Pad, y);
            using var dot = new SolidBrush(Theme.Accent); g.FillEllipse(dot, Pad - 3, y - 3, 6, 6);
        }
        g.Clip = saved;

        // scrollbar thumb
        if (MaxScroll > 0)
        {
            var (ty, th) = Thumb();
            using var b = new SolidBrush(Theme.Blend(Theme.PanelBg, Theme.TextCol, _thumbDrag ? 0.40 : 0.22));
            using var p = Theme.RoundedRect(new RectangleF(Width - ThumbW - 3, ty, ThumbW, th), ThumbW / 2f);
            g.FillPath(b, p);
        }

        // optional hint (e.g. Repeat One)
        if (_hint is not null && _items.Count > 0)
        {
            using var pen = new Pen(Theme.HairLine); g.DrawLine(pen, Pad, Height - 26, Width - Pad, Height - 26);
            TextRenderer.DrawText(g, _hint, Theme.UiFont(8.25f), new Rectangle(Pad, Height - 25, Width - 2 * Pad, 24), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private static void DrawArt(Graphics g, Bitmap? art, Rectangle r)
    {
        using var clip = Theme.RoundedRect(new RectangleF(r.X, r.Y, r.Width, r.Height), Math.Max(3, r.Width * Theme.TileFrac));
        var saved = g.Clip; g.SetClip(clip, CombineMode.Intersect);
        if (art is not null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(art, r); }
        else using (var ph = new LinearGradientBrush(r, Theme.Blend(Theme.PanelBg, Color.White, 0.07), Theme.Blend(Theme.PanelBg, Color.Black, 0.18), 60f)) g.FillRectangle(ph, r);
        g.Clip = saved;
    }

    private static void DrawX(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, r.Width / 2f); g.FillPath(hb, hp); }
        using var pen = new Pen(hover ? Theme.TextCol : Theme.Subtle, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 4.5f;
        g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
        g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
    }

    // ---- interaction ----
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (MaxScroll <= 0) return;
        _scrollY = Math.Clamp(_scrollY - Math.Sign(e.Delta) * RowH, 0, MaxScroll);
        UpdateHover(PointToClient(Cursor.Position));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (CloseRect.Contains(e.Location)) { CloseRequested?.Invoke(); return; }
        if (_items.Count > 0 && ClearRect.Contains(e.Location)) { ClearRequested?.Invoke(); return; }
        if (MaxScroll > 0) { var (ty, th) = Thumb(); if (e.X >= Width - ThumbW - 6 && e.Y >= ty && e.Y <= ty + th) { _thumbDrag = true; _thumbGrabY = e.Y; _thumbGrabScroll = _scrollY; return; } }
        int row = RowAt(e.Y);
        if (row >= 0)
        {
            if (RemoveRect(RowTop(row)).Contains(e.Location)) { RemoveRequested?.Invoke(_items[row].t); return; }
            _dragIndex = row; _dragStartY = e.Y; _dropIndex = row;   // potential drag
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_thumbDrag)
        {
            int track = ListViewH - 8; var (_, th) = Thumb();
            double per = MaxScroll / (double)Math.Max(1, track - th);
            _scrollY = Math.Clamp(_thumbGrabScroll + (int)((e.Y - _thumbGrabY) * per), 0, MaxScroll);
            Invalidate(); return;
        }
        if (_dragIndex >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging) { if (Math.Abs(e.Y - _dragStartY) < 5) return; _dragging = true; Cursor = Cursors.SizeNS; }
            if (e.Y < ListTop + 16) _scrollY = Math.Max(0, _scrollY - RowH / 2);          // edge auto-scroll
            else if (e.Y > Height - 16) _scrollY = Math.Min(MaxScroll, _scrollY + RowH / 2);
            _dropIndex = Math.Clamp((e.Y - ListTop + _scrollY + RowH / 2) / RowH, 0, _items.Count);
            Invalidate(); return;
        }
        UpdateHover(e.Location);
    }

    private void UpdateHover(Point p)
    {
        bool hc = CloseRect.Contains(p), hcl = _items.Count > 0 && ClearRect.Contains(p);
        int row = RowAt(p.Y);
        bool hr = row >= 0 && RemoveRect(RowTop(row)).Contains(p);
        if (hc != _hoverClose || hcl != _hoverClear || row != _hoverRow || hr != _hoverRemove)
        {
            _hoverClose = hc; _hoverClear = hcl; _hoverRow = row; _hoverRemove = hr;
            Cursor = (hc || hcl || hr || row >= 0) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_thumbDrag) { _thumbDrag = false; Invalidate(); return; }
        if (_dragging && _dragIndex >= 0 && _dropIndex >= 0 && _dropIndex != _dragIndex)
            MoveRequested?.Invoke(_dragIndex, _dropIndex);
        _dragging = false; _dragIndex = -1; _dropIndex = -1; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int row = RowAt(e.Y);
        if (row >= 0 && !RemoveRect(RowTop(row)).Contains(e.Location)) ActivateRequested?.Invoke(_items[row].t);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1 || _hoverClose || _hoverClear || _hoverRemove)
        { _hoverRow = -1; _hoverClose = _hoverClear = _hoverRemove = false; Invalidate(); }
    }
}

/// <summary>Hosts the <see cref="UpNextPanel"/> in a borderless, DWM-rounded floating popover anchored to the
/// queue button (like the Equalizer / Pro-features flyouts): all corners rounded, the OS drop-shadow, and
/// click-away / Esc / × dismiss. Sizes itself to the queue content (up to a cap, then the panel scrolls).</summary>
internal sealed class UpNextFlyout : FlyoutForm
{
    public event Action? ClearRequested;
    public event Action<Track>? RemoveRequested;
    public event Action<int, int>? MoveRequested;
    public event Action<Track>? ActivateRequested;

    private const int Wide = 304, MinH = 168, MaxH = 560;
    private readonly UpNextPanel _panel;

    public UpNextFlyout()
    {
        ClientSize = new Size(Wide, 420);
        _panel = new UpNextPanel { Dock = DockStyle.Fill };
        _panel.CloseRequested += Close;                                  // the × closes the popover
        _panel.ClearRequested += () => ClearRequested?.Invoke();
        _panel.RemoveRequested += t => RemoveRequested?.Invoke(t);
        _panel.MoveRequested += (from, to) => MoveRequested?.Invoke(from, to);
        _panel.ActivateRequested += t => ActivateRequested?.Invoke(t);
        Controls.Add(_panel);
    }

    /// <summary>Set the queue data. Before the popover is shown it sizes the window to fit the content (capped —
    /// taller queues then scroll inside); once shown the size is fixed so live queue changes don't make it jump.</summary>
    public void SetData(Track? now, Bitmap? nowArt, IReadOnlyList<(Track t, Bitmap? art)> items, string? hint)
    {
        if (!Visible)
        {
            int h = Math.Clamp(UpNextPanel.DesiredHeight(items.Count, now is not null, hint is not null), MinH, MaxH);
            ClientSize = new Size(Wide, h);
        }
        _panel.SetData(now, nowArt, items, hint);
    }
}
