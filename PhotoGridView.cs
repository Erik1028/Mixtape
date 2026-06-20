using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// An owner-painted, scrollable thumbnail grid for the Photos view — rounded tiles, accent
/// selection ring, hover lift, mouse-wheel scrolling, multi-select (Ctrl/Shift), and a
/// right-click event. Renders identically on screen and via DrawToBitmap.
/// </summary>
internal sealed class PhotoGridView : Panel
{
    public event Action? SelectionChanged;
    public event Action<Point>? ItemRightClicked;
    /// <summary>Double-click on a tile — carries the photo id, for opening the full-size viewer.</summary>
    public event Action<uint>? ItemActivated;

    private sealed class Tile { public uint Id; public Bitmap? Thumb; public bool Selected; public float Fade = 1f; public Tween? Tween; }
    private readonly List<Tile> _tiles = new();
    private readonly List<(Rectangle Rect, Tile Tile)> _hit = new();
    private Tile? _hover;
    private int _scroll;
    private int _lastClicked = -1;
    private string _empty = "No photos yet.";
    private bool _barDragging, _barHover;       // grabbable scrollbar state
    private int _barDragStartY, _barDragStartScroll;
    private const int BarZone = 16;             // right-edge hit width for grabbing the scrollbar

    private const int Gap = 14, Pad = 22;
    private int _tileSize = 132;
    /// <summary>TARGET square tile edge in px — the photo-view size slider. The tiles don't render at exactly
    /// this size: it sets how many columns fit, and those columns then stretch to fill the width edge-to-edge
    /// (iTunes-style), so widening the window grows the tiles instead of leaving a gutter. See <see cref="TileEdge"/>.</summary>
    public int TileSize
    {
        get => _tileSize;
        set { int v = Math.Clamp(value, 80, 280); if (v == _tileSize) return; _tileSize = v; _scroll = Math.Min(_scroll, MaxScroll()); Invalidate(); }
    }

    /// <summary>Content width between the side margins.</summary>
    private int AvailW => Math.Max(0, Width - Pad * 2);
    /// <summary>The ACTUAL on-screen square tile edge: the chosen columns stretched to fill <see cref="AvailW"/>
    /// exactly, so the grid reaches edge-to-edge at any window size.</summary>
    private int TileEdge { get { int c = Columns; return Math.Max(40, (AvailW - (c - 1) * Gap) / c); } }
    private int TileW => TileEdge;
    private int TileH => TileEdge;

    public PhotoGridView()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        MouseMove += OnMouseMoveInternal;
        MouseLeave += (_, _) => { _hover = null; if (_barHover) _barHover = false; Invalidate(); };
        MouseWheel += (_, e) => { _scroll = Math.Max(0, Math.Min(MaxScroll(), _scroll - Math.Sign(e.Delta) * 60)); Invalidate(); };
        MouseDown += OnMouseDown;
        MouseUp += (_, _) => { if (_barDragging) { _barDragging = false; Invalidate(); } };
        MouseDoubleClick += (_, e) => { if (e.X < Width - BarZone) { var t = HitTest(e.Location); if (t is not null) ItemActivated?.Invoke(t.Id); } };
    }

    public void SetPhotos(IEnumerable<(uint Id, Bitmap? Thumb)> photos, string emptyText = "No photos yet.")
    {
        foreach (var t in _tiles) { t.Tween?.Cancel(); t.Thumb?.Dispose(); }
        _tiles.Clear();
        foreach (var (id, thumb) in photos) _tiles.Add(new Tile { Id = id, Thumb = thumb });
        _empty = emptyText;
        _scroll = 0; _lastClicked = -1;
        Invalidate();
    }

    /// <summary>Remove the tiles with these photo ids in place — keeping every surviving tile's already-decoded
    /// thumbnail and the current scroll position (just re-clamped). Lets a delete refresh the grid without
    /// rebuilding/re-decoding the whole library or snapping back to the top. Returns how many tiles were removed.</summary>
    public int RemovePhotos(IReadOnlyCollection<uint> ids)
    {
        if (ids.Count == 0 || _tiles.Count == 0) return 0;
        var set = ids as HashSet<uint> ?? new HashSet<uint>(ids);
        int removed = _tiles.RemoveAll(t =>
        {
            if (!set.Contains(t.Id)) return false;
            t.Tween?.Cancel(); t.Thumb?.Dispose();   // Cancel() stops the fade without firing onDone on the dead tile
            return true;
        });
        if (removed == 0) return 0;
        _hover = null;
        _lastClicked = -1;
        _scroll = Math.Min(_scroll, MaxScroll());   // a shorter grid may need less scroll; keep position otherwise
        Invalidate();
        SelectionChanged?.Invoke();                 // the removed tiles were the selection → it's now empty
        return removed;
    }

    /// <summary>Set a tile's thumbnail once it's been decoded in the background (matched by photo id). The thumb
    /// dissolves in over the placeholder glyph so tiles bloom in softly instead of popping.</summary>
    public void SetThumb(uint id, Bitmap thumb)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            if (t.Id != id) continue;
            t.Thumb?.Dispose(); t.Thumb = thumb;
            t.Tween?.Cancel();
            if (Anim.MotionEnabled) { t.Fade = 0f; int idx = i; t.Tween = Anim.Run(220, v => { t.Fade = (float)v; InvalidateTileAt(idx); }, () => { t.Tween = null; t.Fade = 1f; InvalidateTileAt(idx); }, Easings.OutCubic); }
            else t.Fade = 1f;
            InvalidateTileAt(i);   // repaint ONLY this tile, not the whole 1500-tile grid, as each thumb decodes
            return;
        }
        thumb.Dispose(); // no matching tile (list changed) — don't leak
    }

    // Invalidate one tile's rect, computed from the uniform grid + current scroll (correct even if scrolled mid-fade).
    private void InvalidateTileAt(int index)
    {
        if (IsDisposed || index < 0 || index >= _tiles.Count) return;
        int cols = Columns;
        int gridW = cols * TileW + Math.Max(0, cols - 1) * Gap;
        int x0 = Math.Max(Pad, (Width - gridW) / 2);
        int x = x0 + (index % cols) * (TileW + Gap);
        int y = Pad - _scroll + (index / cols) * (TileH + Gap);
        if (y + TileH < 0 || y > Height) return;
        Invalidate(new Rectangle(x - 1, y - 1, TileW + 2, TileH + 2));
    }

    public List<uint> SelectedIds => _tiles.Where(t => t.Selected).Select(t => t.Id).ToList();
    public int Count => _tiles.Count;
    /// <summary>True when every tile has its decoded thumbnail (the background decode finished) — lets the host
    /// reuse the grid on a Photos-view revisit instead of re-decoding the whole library.</summary>
    public bool AllThumbsLoaded => _tiles.Count > 0 && _tiles.All(t => t.Thumb is not null);

    // Choose the column count so the flexed tile size lands as CLOSE to the target as possible (round, not
    // floor): widening the window grows tiles smoothly and only adds a column once tiles would otherwise get
    // noticeably bigger than the target — instead of leaving the extra width as an empty side gutter.
    private int Columns => AvailW <= 0 ? 1 : Math.Max(1, (int)Math.Round((AvailW + Gap) / (double)(_tileSize + Gap)));
    private int Rows => (int)Math.Ceiling(_tiles.Count / (double)Columns);
    private int ContentHeight => Pad * 2 + Rows * TileH + Math.Max(0, Rows - 1) * Gap;
    private int MaxScroll() => Math.Max(0, ContentHeight - Height);

    private Tile? HitTest(Point p)
    {
        foreach (var (rect, tile) in _hit) if (rect.Contains(p)) return tile;
        return null;
    }

    /// <summary>Scrollbar metrics: the max scroll, the thumb height and its current Y — matches the paint code.</summary>
    private (int Max, int BarH, int BarY) Bar()
    {
        int max = MaxScroll();
        if (max <= 0 || ContentHeight <= 0) return (0, 0, 0);
        int barH = Math.Max(30, (int)(Height * ((float)Height / ContentHeight)));
        int barY = (int)((Height - barH) * (_scroll / (float)max));
        return (max, barH, barY);
    }

    private void SetScroll(int value)
    {
        int v = Math.Max(0, Math.Min(MaxScroll(), value));
        if (v != _scroll) { _scroll = v; Invalidate(); }
    }

    private void OnMouseMoveInternal(object? sender, MouseEventArgs e)
    {
        if (_barDragging)
        {
            var (max, barH, _) = Bar();
            if (max > 0)
            {
                double perPx = max / (double)Math.Max(1, Height - barH);
                SetScroll((int)Math.Round(_barDragStartScroll + (e.Y - _barDragStartY) * perPx));
            }
            return;
        }
        bool overBar = Bar().Max > 0 && e.X >= Width - BarZone;
        if (overBar != _barHover) { _barHover = overBar; Invalidate(); }
        var ht = HitTest(e.Location);
        if (!ReferenceEquals(ht, _hover)) { _hover = ht; Invalidate(); }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        Focus();
        // Grabbable scrollbar: drag the thumb, or click the track above/below it to page.
        var (max, barH, barY) = Bar();
        if (e.Button == MouseButtons.Left && max > 0 && e.X >= Width - BarZone)
        {
            if (e.Y >= barY && e.Y <= barY + barH) { _barDragging = true; _barDragStartY = e.Y; _barDragStartScroll = _scroll; }
            else SetScroll(_scroll + (e.Y < barY ? -1 : 1) * (int)(Height * 0.9));
            return;
        }
        var t = HitTest(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (t is not null && !t.Selected) { foreach (var x in _tiles) x.Selected = false; t.Selected = true; }
            Invalidate(); SelectionChanged?.Invoke();
            if (t is not null) ItemRightClicked?.Invoke(PointToScreen(e.Location));
            return;
        }
        if (t is null) { foreach (var x in _tiles) x.Selected = false; Invalidate(); SelectionChanged?.Invoke(); return; }
        int idx = _tiles.IndexOf(t);
        bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
        if (shift && _lastClicked >= 0)
        {
            int a = Math.Min(_lastClicked, idx), b = Math.Max(_lastClicked, idx);
            if (!ctrl) foreach (var x in _tiles) x.Selected = false;
            for (int i = a; i <= b; i++) _tiles[i].Selected = true;
        }
        else if (ctrl) { t.Selected = !t.Selected; _lastClicked = idx; }
        else { foreach (var x in _tiles) x.Selected = false; t.Selected = true; _lastClicked = idx; }
        Invalidate(); SelectionChanged?.Invoke();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); _scroll = Math.Min(_scroll, MaxScroll()); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        _hit.Clear();
        if (_tiles.Count == 0)
        {
            TextRenderer.DrawText(g, _empty, Theme.UiFont(11f), new Rectangle(0, 0, Width, Height), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int cols = Columns;
        // Centre the tile block so the leftover horizontal slack is split evenly (it was all dumped on the
        // right, leaving a big empty gutter with the scrollbar floating in it).
        int gridW = cols * TileW + Math.Max(0, cols - 1) * Gap;
        int x0 = Math.Max(Pad, (Width - gridW) / 2), y0 = Pad - _scroll;
        for (int i = 0; i < _tiles.Count; i++)
        {
            int col = i % cols, row = i / cols;
            int x = x0 + col * (TileW + Gap);
            int y = y0 + row * (TileH + Gap);
            var rect = new Rectangle(x, y, TileW, TileH);
            _hit.Add((rect, _tiles[i]));
            if (y + TileH < 0 || y > Height) continue; // cull off-screen
            DrawTile(g, rect, _tiles[i]);
        }

        // thin scroll indicator
        int max = MaxScroll();
        if (max > 0)
        {
            float frac = (float)Height / ContentHeight;
            int barH = Math.Max(30, (int)(Height * frac));
            int barY = (int)((Height - barH) * (_scroll / (float)max));
            bool active = _barDragging || _barHover;
            using var b = new SolidBrush(Color.FromArgb(active ? 165 : 90, 255, 255, 255));
            float w = active ? 6 : 4;
            using var p = Theme.RoundedRect(new RectangleF(Width - 5 - w, barY + 2, w, barH - 4), w / 2f);
            g.FillPath(b, p);
        }
    }

    private void DrawTile(Graphics g, Rectangle rect, Tile t)
    {
        bool hover = ReferenceEquals(t, _hover);
        var img = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        using (var path = Theme.RoundedRect(img, 10))
        {
            using (var bg = new SolidBrush(hover ? Theme.RowHover : Theme.PanelBg)) g.FillPath(bg, path);
            if (t.Thumb is not null)
            {
                if (t.Fade < 1f)   // placeholder glyph shows through while the freshly-decoded thumb dissolves in
                {
                    using var gf0 = Theme.UiFont(20f);
                    TextRenderer.DrawText(g, "▦", gf0, rect, Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                var prev = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // COVER-fit: scale so the photo fills the whole tile, centred, with the overflow cropped by the
                // rounded clip below — so landscape/portrait shots zoom to fill the frame instead of sitting
                // letterboxed with black bars. (Aspect is preserved; only the long edge is cropped.)
                double s = Math.Max((double)rect.Width / t.Thumb.Width, (double)rect.Height / t.Thumb.Height);
                int w = Math.Max(1, (int)Math.Ceiling(t.Thumb.Width * s)), h = Math.Max(1, (int)Math.Ceiling(t.Thumb.Height * s));
                var dest = new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
                var clip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                if (t.Fade < 1f) Theme.DrawImageAlpha(g, t.Thumb, new RectangleF(dest.X, dest.Y, dest.Width, dest.Height), t.Fade);
                else g.DrawImage(t.Thumb, dest);
                g.Clip = clip;
                g.InterpolationMode = prev;
            }
            else
            {
                using var gf = Theme.UiFont(20f);
                TextRenderer.DrawText(g, "▦", gf, rect, Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        if (t.Selected)
        {
            using var pen = new Pen(Theme.Accent, 3);
            using var path = Theme.RoundedRect(new RectangleF(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), 10);
            g.DrawPath(pen, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var t in _tiles) { t.Tween?.Cancel(); t.Thumb?.Dispose(); }
        base.Dispose(disposing);
    }
}
