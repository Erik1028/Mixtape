using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// An owner-painted cover grid for browsing by Album or Artist: each tile is a rounded cover with a
/// bold title and a dim subtitle beneath. Wheel + a grabbable scrollbar; double-click (or click) a
/// tile to drill in. Covers load in the background via <see cref="SetCover"/>. Mirrors the look and
/// the scrollbar interaction of <see cref="PhotoGridView"/>.
/// </summary>
internal sealed class BrowseGridView : Panel
{
    public event Action<string>? ItemActivated; // carries the card Key
    public event Action? Scrolled;               // so the host can refresh the frosted-bar backdrop

    private sealed class Card { public string Key = ""; public string Title = ""; public string Subtitle = ""; public Bitmap? Cover; public int Seed; public string? Initials;
        public float Fade = 1f; public Bitmap? Prev; public Tween? Tween; }   // Prev/Cover are cache-borrowed (never disposed); cross-dissolve state
    private readonly List<Card> _cards = new();
    private readonly List<(Rectangle Rect, Card Card)> _hit = new();
    private Card? _hover, _leaving;     // the card under the pointer, and the one it just left
    private float _hoverT, _leaveT;     // each card's own share of the lift, so neither snaps
    private Tween? _hoverTw, _leaveTw;
    private int _scroll;
    private string _empty = "Nothing here yet.";

    private bool _barDragging, _barHover;
    private int _barDragStartY, _barDragStartScroll;
    private const int BarZone = 16;
    // Cached fonts — the title/subtitle fonts were allocated PER CARD (and undisposed) every repaint.
    private readonly Font _fTitle = Theme.UiFont(9.5f, FontStyle.Bold), _fSub = Theme.UiFont(Theme.SzCaption), _fEmpty = Theme.UiFont(Theme.SzBody);

    private const int Pad = 22, Gap = 16, TextH = 42; // 22 = the one frame every page keeps to the card edge
    private const int TargetCover = 132;              // desired cover edge — the actual edge flexes to fill the width
    private int TileH => CoverW + TextH;

    /// <summary>Content width between the side margins.</summary>
    private int AvailW => Math.Max(0, Width - Pad * 2);
    /// <summary>The ACTUAL cover edge: the chosen columns stretched to fill the width edge-to-edge (iTunes-style),
    /// so widening the window grows the covers instead of leaving an empty side gutter.</summary>
    private int CoverW { get { int c = Columns; return Math.Max(64, (AvailW - (c - 1) * Gap) / c); } }

    public BrowseGridView()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        MouseMove += OnMove;
        MouseLeave += (_, _) => { SetHover(null); if (_barHover) _barHover = false; Invalidate(); };
        MouseWheel += (_, e) => Glide(e.Delta);
        MouseDown += OnDown;
        MouseUp += (_, _) => { if (_barDragging) { _barDragging = false; Invalidate(); } };
        MouseDoubleClick += (_, e) => { if (e.X < Width - BarZone) { var c = HitTest(e.Location); if (c is not null) ItemActivated?.Invoke(c.Key); } };
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && e.X < Width - BarZone) { var c = HitTest(e.Location); if (c is not null) ItemActivated?.Invoke(c.Key); } };
    }

    public void SetItems(IEnumerable<(string Key, string Title, string Subtitle)> items, string emptyText = "Nothing here yet.")
    {
        // Covers are BORROWED from ArtworkService's permanent cache (same contract as the track rows /
        // header / sidebar) — this view must NEVER dispose them, or revisiting the grid draws a freed bitmap.
        foreach (var c in _cards) c.Tween?.Cancel();
        _cards.Clear();
        foreach (var (key, title, sub) in items)
            _cards.Add(new Card { Key = key, Title = title, Subtitle = sub, Seed = Theme.StableHash(title + sub), Initials = Theme.Initials(title, sub) });
        _empty = emptyText;
        _scroll = 0; _scrollTarget = 0; _scrollTw?.Cancel(); _scrollTw = null;
        _hoverTw?.Cancel(); _leaveTw?.Cancel(); _hoverTw = _leaveTw = null;
        _hover = _leaving = null; _hoverT = _leaveT = 0;
        Invalidate();
    }

    /// <summary>Attach a cover to a tile. <paramref name="animate"/> = true cross-dissolves it in (for covers that
    /// arrive asynchronously after the view is shown); false applies it instantly (for already-cached covers set
    /// BEFORE the view-switch snapshot, so they're present in the transition instead of popping in after it).
    /// The cover is BORROWED from the ArtworkService cache — assign, never dispose.</summary>
    public void SetCover(string key, Bitmap cover, bool animate = true)
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            if (c.Key != key) continue;
            if (ReferenceEquals(c.Cover, cover)) return;
            c.Tween?.Cancel(); c.Tween = null;
            if (!animate || !Anim.MotionEnabled) { c.Cover = cover; c.Prev = null; c.Fade = 1f; InvalidateCardAt(i); return; }
            c.Prev = c.Cover ?? Theme.MakeArt(CoverW, c.Seed, c.Initials);   // outgoing (cache-owned) — dissolve from it
            c.Cover = cover;
            c.Fade = 0f;
            int idx = i;   // repaint ONLY this tile each frame, not the whole grid (many covers stream in at once)
            c.Tween = Anim.Run(220, v => { c.Fade = (float)v; InvalidateCardAt(idx); },
                () => { c.Tween = null; c.Prev = null; c.Fade = 1f; InvalidateCardAt(idx); }, Easings.OutCubic);
            return;
        }
    }

    // Invalidate just one tile's cover square (computed from the uniform grid + current scroll, so it stays correct
    // even if the user scrolls mid-fade). Off-screen tiles are skipped (nothing to repaint).
    private void InvalidateCardAt(int index)
    {
        if (IsDisposed || index < 0 || index >= _cards.Count) return;
        int cols = Columns;
        int gridW = cols * CoverW + Math.Max(0, cols - 1) * Gap;
        int x0 = Math.Max(Pad, (Width - gridW) / 2);
        int x = x0 + (index % cols) * (CoverW + Gap);
        int y = Pad - _scroll + (index / cols) * (TileH + Gap);
        if (y + CoverW < 0 || y > Height) return;
        Invalidate(new Rectangle(x - 2, y - 2, CoverW + 4, CoverW + 4));
    }

    public int Count => _cards.Count;

    // Columns chosen so the flexed cover lands as close to the target as possible (round, not floor): widening
    // the window grows the covers smoothly and only adds a column once they'd get noticeably bigger than target.
    private int Columns => AvailW <= 0 ? 1 : Math.Max(1, (int)Math.Round((AvailW + Gap) / (double)(TargetCover + Gap)));
    private int Rows => (int)Math.Ceiling(_cards.Count / (double)Columns);
    private int ContentHeight => Pad * 2 + Rows * TileH + Math.Max(0, Rows - 1) * Gap;

    // ---- wheel scrolling that glides ----
    // The song list has eased its wheel since the redesign; these pages jumped a notch at a time, which is
    // the roughness you feel most because you feel it constantly. A notch now moves the TARGET (so spinning
    // fast adds up instead of restarting) and the drawn offset chases it.
    private int _scrollTarget;
    private Tween? _scrollTw;

    private void Glide(int delta)
    {
        int step = Math.Max(40, Height / 6);
        SetScroll(_scrollTarget - Math.Sign(delta) * step, animate: true);
    }

    private int MaxScroll() => Math.Max(0, ContentHeight - Height);

    private Card? HitTest(Point p) { foreach (var (rect, card) in _hit) if (rect.Contains(p)) return card; return null; }

    private void SetScroll(int v, bool animate = false)
    {
        v = Math.Max(0, Math.Min(MaxScroll(), v));
        _scrollTarget = v;
        if (!animate || !Anim.MotionEnabled || v == _scroll)
        {
            _scrollTw?.Cancel(); _scrollTw = null;
            if (v != _scroll) { _scroll = v; Invalidate(); Scrolled?.Invoke(); }
            return;
        }
        _scrollTw?.Cancel();
        int from = _scroll;
        _scrollTw = Anim.Run(150, t =>
        {
            if (IsDisposed) return;
            _scroll = (int)Math.Round(from + (v - from) * t);
            Invalidate(); Scrolled?.Invoke();
        }, () => _scrollTw = null, Easings.OutQuint);
    }

    /// <summary>Hand the lift from one card to the next: the arriving card rises, the leaving one settles
    /// back. Two tweens, because a card that snapped flat the moment the pointer left looked broken.</summary>
    private void SetHover(Card? c)
    {
        _leaving = _hover; _leaveT = _hoverT;
        _hover = c; _hoverT = 0;
        _hoverTw?.Cancel(); _leaveTw?.Cancel();
        if (!Anim.MotionEnabled) { _hoverT = c is null ? 0 : 1; _leaveT = 0; _leaving = null; Invalidate(); return; }
        if (_leaving is not null)
        {
            float from = _leaveT;
            _leaveTw = Anim.Run(120, v => { if (IsDisposed) return; _leaveT = (float)(from * (1 - v)); Invalidate(); },
                () => { _leaveTw = null; _leaving = null; }, Easings.OutCubic);
        }
        if (c is not null)
            _hoverTw = Anim.Run(110, v => { if (IsDisposed) return; _hoverT = (float)v; Invalidate(); }, () => _hoverTw = null, Easings.OutQuint);
    }

    private float LiftOf(Card c) => ReferenceEquals(c, _hover) ? _hoverT : ReferenceEquals(c, _leaving) ? _leaveT : 0f;

    /// <summary>Where a card's cover sits right now and a COPY of what is drawn in it (the caller owns the
    /// bitmap). Null when that card is not on screen - there is nothing to fly then.</summary>
    public (Rectangle Rect, Bitmap Image)? CoverSpot(string key)
    {
        foreach (var (r, c) in _hit)
        {
            if (c.Key != key) continue;
            var rect = new Rectangle(r.X, r.Y, CoverW, CoverW);
            var bmp = new Bitmap(CoverW, CoverW);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(c.Cover ?? Theme.MakeArt(CoverW, c.Seed, c.Initials), new Rectangle(0, 0, CoverW, CoverW));   // cache-owned: drawn, never disposed
            }
            return (rect, bmp);
        }
        return null;
    }

    private (int Max, int BarH, int BarY) Bar()
    {
        int max = MaxScroll();
        if (max <= 0 || ContentHeight <= 0) return (0, 0, 0);
        int barH = Math.Max(30, (int)(Height * ((float)Height / ContentHeight)));
        int barY = (int)((Height - barH) * (_scroll / (float)max));
        return (max, barH, barY);
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        if (_barDragging)
        {
            var (max, barH, _) = Bar();
            if (max > 0) { double per = max / (double)Math.Max(1, Height - barH); SetScroll((int)Math.Round(_barDragStartScroll + (e.Y - _barDragStartY) * per)); }
            return;
        }
        bool overBar = Bar().Max > 0 && e.X >= Width - BarZone;
        if (overBar != _barHover) { _barHover = overBar; Invalidate(); }
        var c = HitTest(e.Location);
        if (!ReferenceEquals(c, _hover)) SetHover(c);
    }

    private void OnDown(object? s, MouseEventArgs e)
    {
        Focus();
        var (max, barH, barY) = Bar();
        if (e.Button == MouseButtons.Left && max > 0 && e.X >= Width - BarZone)
        {
            if (e.Y >= barY && e.Y <= barY + barH) { _barDragging = true; _barDragStartY = e.Y; _barDragStartScroll = _scroll; }
            else SetScroll(_scroll + (e.Y < barY ? -1 : 1) * (int)(Height * 0.9));
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); _scroll = Math.Min(_scroll, MaxScroll()); Invalidate(); }


    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        _hit.Clear();
        if (_cards.Count == 0)
        {
            Theme.DrawEmptyState(g, ClientRectangle, _empty, null);
            return;
        }

        int cols = Columns;
        // Centre the tile block so the leftover horizontal slack is split evenly into left/right margins,
        // instead of all dumping on the right (matches PhotoGridView, which fixed the same gutter).
        int gridW = cols * CoverW + Math.Max(0, cols - 1) * Gap;
        int x0 = Math.Max(Pad, (Width - gridW) / 2);
        for (int i = 0; i < _cards.Count; i++)
        {
            int col = i % cols, row = i / cols;
            int x = x0 + col * (CoverW + Gap);
            int y = Pad - _scroll + row * (TileH + Gap);
            var tile = new Rectangle(x, y, CoverW, TileH);
            _hit.Add((tile, _cards[i]));
            if (y + TileH < 0 || y > Height) continue; // cull off-screen
            DrawCard(g, x, y, _cards[i]);
        }

        Theme.DrawScrollEdge(g, 0, 0, Width, _scroll);   // scroll edge: cards dissolve into the chrome under the header

        // grabbable scrollbar
        var (max, barH, barY) = Bar();
        if (max > 0)
        {
            bool active = _barDragging || _barHover;
            using var b = new SolidBrush(Color.FromArgb(active ? 165 : 90, 255, 255, 255));
            float w = active ? 6 : 4;
            using var p = Theme.RoundedRect(new RectangleF(Width - 5 - w, barY + 2, w, barH - 4), w / 2f);
            g.FillPath(b, p);
        }
    }

    private void DrawCard(Graphics g, int x, int y, Card c)
    {
        float lift = LiftOf(c);
        int grow = (int)Math.Round(5 * lift);        // the cover grows around its own centre
        var cover = new Rectangle(x - grow, y - grow, CoverW + 2 * grow, CoverW + 2 * grow);
        int cr = (int)Math.Round(cover.Width * Theme.TileFrac);
        bool hover = ReferenceEquals(c, _hover);
        if (lift > 0.01f)   // a soft ground shadow: three rings, so it reads as depth and not as a halo
        {
            var sm0 = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 3; i >= 1; i--)
            {
                using var sh = new SolidBrush(Color.FromArgb((int)(16 * lift), 0, 0, 0));
                using var sp = Theme.RoundedRect(new RectangleF(cover.X - i, cover.Y + i + 1, cover.Width + 2 * i, cover.Height + i), cr + i);
                g.FillPath(sh, sp);
            }
            g.SmoothingMode = sm0;
        }
        using (var path = Theme.RoundedRect(cover, cr))
        {
            using var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Both the cover (ArtworkService cache) and the MakeArt fallback (Theme.ArtCache) are
            // cache-owned — NEVER dispose them (a `using` here freed the cached gradient, so the next
            // repaint drew a disposed bitmap → "Parameter is not valid").
            if (c.Prev is not null && c.Fade < 1f)
            {
                g.DrawImage(c.Prev, cover);                                                                          // outgoing holds
                Theme.DrawImageAlpha(g, c.Cover ?? Theme.MakeArt(CoverW, c.Seed, c.Initials), new RectangleF(cover.X, cover.Y, cover.Width, cover.Height), c.Fade); // incoming dissolves in
            }
            else g.DrawImage(c.Cover ?? Theme.MakeArt(CoverW, c.Seed, c.Initials), cover);
            g.Clip = clip;
        }
        if (lift > 0.01f) { using var hp = Theme.RoundedRect(cover, cr); using var hb = new SolidBrush(Color.FromArgb((int)(36 * lift), 255, 255, 255)); g.FillPath(hb, hp); }
        using (var bp = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.08))) { using var p2 = Theme.RoundedRect(new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 1, cover.Height - 1), cr); g.DrawPath(bp, p2); }

        TextRenderer.DrawText(g, c.Title, _fTitle,
            new Rectangle(x, y + CoverW + 6, CoverW, 18), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, c.Subtitle, _fSub,
            new Rectangle(x, y + CoverW + 24, CoverW, 16), Theme.Subtle,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Render the row(s) of cards just BELOW the visible fold — the "continuation" that flows into the
    /// now-playing bar's frosted glass (same idea as the song list). Cheap (only the next row is drawn), so the host
    /// can re-cut it on every scroll without a full DrawToBitmap. Null when there's nothing below (at the bottom).</summary>
    public Bitmap? RenderFrostStrip(int stripH)
    {
        if (Width < 8 || stripH < 4 || _cards.Count == 0) return null;
        int contentY = _scroll + Height;                 // content y just past the visible bottom
        if (contentY >= ContentHeight - Pad) return null; // scrolled to the end → nothing below to show
        int cols = Columns;
        int gridW = cols * CoverW + Math.Max(0, cols - 1) * Gap;
        int x0 = Math.Max(Pad, (Width - gridW) / 2);
        var bmp = new Bitmap(Width, stripH);
        bool drawn = false;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, Width, stripH);
            for (int i = 0; i < _cards.Count; i++)
            {
                int y = Pad + (i / cols) * (TileH + Gap) - contentY;
                if (y + TileH < 0 || y >= stripH) continue;
                DrawCard(g, x0 + (i % cols) * (CoverW + Gap), y, _cards[i]);
                drawn = true;
            }
        }
        if (!drawn) { bmp.Dispose(); return null; }
        return bmp;
    }

    // No cover disposal: every Card.Cover/Prev is a borrowed ArtworkService / Theme.ArtCache instance.
    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var c in _cards) c.Tween?.Cancel(); _fTitle.Dispose(); _fSub.Dispose(); _fEmpty.Dispose(); }
        base.Dispose(disposing);
    }
}
