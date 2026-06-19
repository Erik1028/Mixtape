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

    private sealed class Tile { public uint Id; public Bitmap? Thumb; public bool Selected; }
    private readonly List<Tile> _tiles = new();
    private readonly List<(Rectangle Rect, Tile Tile)> _hit = new();
    private Tile? _hover;
    private int _scroll;
    private int _lastClicked = -1;
    private string _empty = "No photos yet.";
    private bool _barDragging, _barHover;       // grabbable scrollbar state
    private int _barDragStartY, _barDragStartScroll;
    private const int BarZone = 16;             // right-edge hit width for grabbing the scrollbar

    private const int TileW = 132, TileH = 132, Gap = 14, Pad = 22;

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
        foreach (var t in _tiles) t.Thumb?.Dispose();
        _tiles.Clear();
        foreach (var (id, thumb) in photos) _tiles.Add(new Tile { Id = id, Thumb = thumb });
        _empty = emptyText;
        _scroll = 0; _lastClicked = -1;
        Invalidate();
    }

    /// <summary>Set a tile's thumbnail once it's been decoded in the background (matched by photo id).</summary>
    public void SetThumb(uint id, Bitmap thumb)
    {
        foreach (var t in _tiles)
            if (t.Id == id) { t.Thumb?.Dispose(); t.Thumb = thumb; Invalidate(); return; }
        thumb.Dispose(); // no matching tile (list changed) — don't leak
    }

    public List<uint> SelectedIds => _tiles.Where(t => t.Selected).Select(t => t.Id).ToList();
    public int Count => _tiles.Count;

    private int Columns => Math.Max(1, (Width - Pad * 2 + Gap) / (TileW + Gap));
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
                var prev = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // fit the thumb into the tile, centered, preserving aspect — never enlarge past 1:1
                // (small native thumbs like the 64×64 Classic/Nano slot stay crisp instead of going blurry).
                double s = Math.Min(1.0, Math.Min((double)(rect.Width - 12) / t.Thumb.Width, (double)(rect.Height - 12) / t.Thumb.Height));
                int w = Math.Max(1, (int)(t.Thumb.Width * s)), h = Math.Max(1, (int)(t.Thumb.Height * s));
                var dest = new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
                var clip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                g.DrawImage(t.Thumb, dest);
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
}
