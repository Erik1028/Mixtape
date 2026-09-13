using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The home page: an overview instead of the raw song list — shelves of recently added albums (the iPod's,
/// the PC's), a "continue listening" card for the song that was playing when Mixtape last closed, and a short
/// list of the most-played (or most recently played) songs. Owner-painted like <see cref="BrowseGridView"/>:
/// wheel + a grabbable scrollbar, covers stream in via <see cref="SetCover"/>. Every tile and row leads to an
/// existing view — the page is a front door, not a new place.
/// </summary>
internal sealed class HomeView : Panel
{
    public sealed class Tile
    {
        public string Key = "", Title = "", Subtitle = ""; public bool Local;
        public Bitmap? Cover, Prev; public float Fade = 1f; public Tween? Tween; public int Seed; public string? Initials;   // covers are cache-borrowed (never disposed)
    }
    public sealed class Strip { public string Label = ""; public readonly List<Tile> Tiles = new(); }
    public sealed class Row { public Track Track = null!; public string Title = "", Sub = "", Figure = ""; public bool Accent; }
    public sealed class Resume { public Track Track = null!; public string Title = "", Sub = "", Times = ""; public Bitmap? Cover; public double Seconds; }

    public event Action<Tile>? TileActivated;
    public event Action<Track>? TrackActivated;
    public event Action<Resume>? ResumeRequested;
    public event Action? Scrolled;

    private readonly List<Strip> _strips = new();
    private readonly List<Row> _rows = new();
    private string _listLabel = "";
    private Resume? _resume;
    private readonly ThemedButton _resumeBtn = new() { Primary = true, Height = 30, Text = Loc.T("Continue"), Visible = false };

    private readonly List<(Rectangle Rect, object Target)> _hit = new();
    private object? _hover;
    private int _scroll;
    private bool _barDragging, _barHover;
    private int _barDragStartY, _barDragStartScroll;
    private int _contentH;

    private const int Pad = 22, Gap = 16, TextH = 42, LabelH = 22, RowH = 26, CardH = 70, SectionGap = 26, BarZone = 16;
    private const int TargetCover = 118, MaxCover = 150, MinCols = 3, MaxCols = 8;
    private readonly Font _fLabel = Theme.UiFont(Theme.SzLabel, FontStyle.Bold);
    private readonly Font _fTitle = Theme.UiFont(9.5f, FontStyle.Bold), _fSub = Theme.UiFont(Theme.SzCaption);
    private readonly Font _fRow = Theme.UiFont(Theme.SzTitle, FontStyle.Bold), _fFig = Theme.UiFont(Theme.SzCaption, FontStyle.Bold);
    private readonly Font _fCardTitle = Theme.UiFont(10.5f, FontStyle.Bold);

    public HomeView()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(_resumeBtn);
        _resumeBtn.Click += (_, _) => { if (_resume is not null) ResumeRequested?.Invoke(_resume); };
        MouseMove += OnMove;
        MouseLeave += (_, _) => { _hover = null; if (_barHover) _barHover = false; Invalidate(); };
        MouseWheel += (_, e) => SetScroll(_scroll - Math.Sign(e.Delta) * 60);
        MouseDown += OnDown;
        MouseUp += (_, _) => { if (_barDragging) { _barDragging = false; Invalidate(); } };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.X >= Width - BarZone) return;
            switch (HitTest(e.Location))
            {
                case Tile t: TileActivated?.Invoke(t); break;
                case Row r: TrackActivated?.Invoke(r.Track); break;
                case Resume rs: ResumeRequested?.Invoke(rs); break;
            }
        };
    }

    /// <summary>Everything on the page, in reading order: the first shelf, then the continue card + the list side by
    /// side, then the remaining shelves. Any part may be empty; an empty page draws the quiet note instead.</summary>
    public void SetContent(IReadOnlyList<Strip> strips, Resume? resume, string listLabel, IReadOnlyList<Row> rows)
    {
        foreach (var s in _strips) foreach (var t in s.Tiles) t.Tween?.Cancel();
        _strips.Clear(); _strips.AddRange(strips);
        _rows.Clear(); _rows.AddRange(rows);
        _listLabel = listLabel;
        _resume = resume;
        _resumeBtn.Visible = resume is not null;
        _scroll = 0; _hover = null;
        Invalidate();
    }

    public bool IsEmpty => _strips.Count == 0 && _rows.Count == 0 && _resume is null;

    /// <summary>Attach a cover to every tile with this key (a cover is BORROWED from the ArtworkService cache — assign,
    /// never dispose). <paramref name="animate"/> cross-dissolves it in; false applies it instantly (already-cached art).</summary>
    public void SetCover(string key, Bitmap cover, bool animate = true)
    {
        foreach (var s in _strips)
            foreach (var t in s.Tiles)
            {
                if (t.Key != key || ReferenceEquals(t.Cover, cover)) continue;
                t.Tween?.Cancel(); t.Tween = null;
                if (!animate || !Anim.MotionEnabled) { t.Cover = cover; t.Prev = null; t.Fade = 1f; Invalidate(); continue; }
                var tile = t;
                t.Prev = t.Cover ?? Theme.MakeArt(CoverW, t.Seed, t.Initials);
                t.Cover = cover; t.Fade = 0f;
                t.Tween = Anim.Run(220, v => { tile.Fade = (float)v; if (!IsDisposed) Invalidate(); },
                    () => { tile.Tween = null; tile.Prev = null; tile.Fade = 1f; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
            }
        if (_resume is not null && _resume.Cover is null && key == ResumeKey) { _resume.Cover = cover; Invalidate(); }
    }

    /// <summary>The continue card's cover is keyed like a tile ("ipod:"/"pc:" + album key) by the host.</summary>
    public string? ResumeKey { get; set; }

    // ---- geometry ----
    private int AvailW => Math.Max(0, Width - Pad * 2);
    private int Cols => Math.Clamp((AvailW + Gap) / (TargetCover + Gap), MinCols, MaxCols);
    private int CoverW => Math.Clamp((AvailW - (Cols - 1) * Gap) / Cols, 64, MaxCover);
    private int TileH => CoverW + TextH;
    private int MaxScroll() => Math.Max(0, _contentH - Height);
    private bool TwoColumns => AvailW >= 680;

    private object? HitTest(Point p) { foreach (var (rect, target) in _hit) if (rect.Contains(p)) return target; return null; }
    private void SetScroll(int v) { v = Math.Max(0, Math.Min(MaxScroll(), v)); if (v != _scroll) { _scroll = v; Invalidate(); Scrolled?.Invoke(); } }

    private (int Max, int BarH, int BarY) Bar()
    {
        int max = MaxScroll();
        if (max <= 0 || _contentH <= 0) return (0, 0, 0);
        int barH = Math.Max(30, (int)(Height * ((float)Height / _contentH)));
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
        var h = overBar ? null : HitTest(e.Location);
        Cursor = h is not null ? Cursors.Hand : Cursors.Default;
        if (!ReferenceEquals(h, _hover)) { _hover = h; Invalidate(); }
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

    // ---- paint (the layout is computed here, once per frame — one source of truth for drawing and hit-testing) ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        _hit.Clear();
        if (IsEmpty)
        {
            _resumeBtn.Visible = false;
            Theme.DrawEmptyState(g, ClientRectangle, Loc.T("Nothing to show yet"), Loc.T("Albums you add and songs you play will show up here."));
            return;
        }

        int x0 = Pad, w = AvailW;
        int y = Pad - _scroll;
        int si = 0;
        if (_strips.Count > 0) y = DrawStrip(g, x0, y, w, _strips[si++]);

        // the continue card + the list, side by side when there is room
        bool hasList = _rows.Count > 0;
        if (_resume is not null || hasList)
        {
            int blockTop = y;
            if (_resume is not null && hasList && TwoColumns)
            {
                int cardW = Math.Min(460, (int)(w * 0.46));
                int cardBottom = DrawResume(g, x0, y, cardW);
                int listBottom = DrawList(g, x0 + cardW + 32, y, w - cardW - 32);
                y = Math.Max(cardBottom, listBottom);
            }
            else
            {
                if (_resume is not null) y = DrawResume(g, x0, y, Math.Min(560, w));
                if (_resume is not null && hasList) y += SectionGap;
                if (hasList) y = DrawList(g, x0, y, w);
            }
            if (y > blockTop) y += SectionGap;
        }
        else _resumeBtn.Visible = false;

        for (; si < _strips.Count; si++) y = DrawStrip(g, x0, y, w, _strips[si]);

        _contentH = y + _scroll + Pad - SectionGap;
        Theme.DrawScrollEdge(g, 0, 0, Width, _scroll);   // scroll edge: the page dissolves into the chrome under the header

        var (max, barH, barY) = Bar();
        if (max > 0)
        {
            bool active = _barDragging || _barHover;
            using var b = new SolidBrush(Color.FromArgb(active ? 165 : 90, 255, 255, 255));
            float bw = active ? 6 : 4;
            using var p = Theme.RoundedRect(new RectangleF(Width - 5 - bw, barY + 2, bw, barH - 4), bw / 2f);
            g.FillPath(b, p);
        }
    }

    private void DrawLabel(Graphics g, int x, int y, int w, string text) =>
        TextRenderer.DrawText(g, text.ToUpperInvariant(), _fLabel, new Rectangle(x, y, w, LabelH), Theme.Faint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

    /// <summary>A shelf: the label, then as many covers as fit on one row (the covers flex to fill the width).</summary>
    private int DrawStrip(Graphics g, int x, int y, int w, Strip s)
    {
        if (s.Tiles.Count == 0) return y;
        DrawLabel(g, x, y, w, s.Label);
        int ty = y + LabelH;
        int cover = CoverW, n = Math.Min(Cols, s.Tiles.Count);
        for (int i = 0; i < n; i++)
        {
            var t = s.Tiles[i];
            int tx = x + i * (cover + Gap);
            var rect = new Rectangle(tx, ty, cover, TileH);
            _hit.Add((rect, t));
            if (ty + TileH < 0 || ty > Height) continue;
            DrawTile(g, tx, ty, cover, t);
        }
        return ty + TileH + SectionGap;
    }

    private void DrawTile(Graphics g, int x, int y, int cw, Tile t)
    {
        var cover = new Rectangle(x, y, cw, cw);
        int cr = (int)Math.Round(cw * Theme.TileFrac);
        bool hover = ReferenceEquals(t, _hover);
        using (var path = Theme.RoundedRect(cover, cr))
        {
            using var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var art = t.Cover ?? Theme.MakeArt(cw, t.Seed, t.Initials);   // cache-owned, never disposed
            if (t.Prev is not null && t.Fade < 1f)
            {
                g.DrawImage(t.Prev, cover);
                Theme.DrawImageAlpha(g, art, new RectangleF(cover.X, cover.Y, cover.Width, cover.Height), t.Fade);
            }
            else g.DrawImage(art, cover);
            g.Clip = clip;
        }
        if (hover) { using var hp = Theme.RoundedRect(cover, cr); using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255)); g.FillPath(hb, hp); }
        using (var bp = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.08))) { using var p2 = Theme.RoundedRect(new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 1, cover.Height - 1), cr); g.DrawPath(bp, p2); }
        TextRenderer.DrawText(g, t.Title, _fTitle, new Rectangle(x, y + cw + 6, cw, 18), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, t.Subtitle, _fSub, new Rectangle(x, y + cw + 24, cw, 16), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>"Continue listening": the song that was playing when Mixtape closed, where it was.</summary>
    private int DrawResume(Graphics g, int x, int y, int w)
    {
        var r = _resume!;
        DrawLabel(g, x, y, w, Loc.T("Continue listening"));
        int cy = y + LabelH;
        var card = new Rectangle(x, cy, w, CardH);
        _hit.Add((card, r));
        bool hover = ReferenceEquals(r, _hover);
        var cardF = new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1);
        var cardCol = Theme.Blend(Theme.Bg, Color.White, hover ? 0.08 : 0.05);
        using (var fill = new SolidBrush(cardCol))
        using (var cp = Theme.RoundedRect(cardF, Theme.RadShell)) g.FillPath(fill, cp);
        _resumeBtn.Surface = cardCol;   // the button clears its corners to the CARD, not to the page behind it
        using (var line = new Pen(Color.FromArgb(28, 255, 255, 255)))
        using (var cp = Theme.RoundedRect(cardF, Theme.RadShell)) g.DrawPath(line, cp);
        var art = new Rectangle(card.X + 8, card.Y + 8, CardH - 16, CardH - 16);
        NowPlayingBar.DrawCoverTile(g, art, false, r.Cover, null, 1f, Theme.StableHash(r.Title));
        int btnW = Math.Max(96, _resumeBtn.NeededWidth + 12);
        int tx = art.Right + 14, tw = card.Right - 12 - btnW - tx;
        TextRenderer.DrawText(g, r.Title, _fCardTitle, new Rectangle(tx, card.Y + 13, tw, 20), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        int timesW = r.Times.Length > 0 ? TextRenderer.MeasureText(r.Times, _fSub, new Size(200, 18), TextFormatFlags.NoPrefix).Width + 4 : 0;   // the position never gets cut: it has its own slot
        int subW = Math.Max(0, tw - timesW - 12);
        // "Artist  •  Album" with no room for the album read as "Tom Petty  •  ..." — the album is DROPPED
        // instead of being cut to an ellipsis, the way the deck's card does it.
        string sub = r.Sub;
        if (TextRenderer.MeasureText(sub, _fSub, new Size(int.MaxValue, 18), TextFormatFlags.NoPrefix).Width > subW
            && sub.IndexOf("  •  ", StringComparison.Ordinal) is int sep && sep > 0) sub = sub[..sep];
        TextRenderer.DrawText(g, sub, _fSub, new Rectangle(tx, card.Y + 37, subW, 18), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        if (timesW > 0) TextRenderer.DrawText(g, r.Times, _fSub, new Rectangle(tx + tw - timesW, card.Y + 37, timesW, 18), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        _resumeBtn.Visible = true;
        _resumeBtn.SetBounds(card.Right - 12 - btnW, card.Y + (CardH - 30) / 2, btnW, 30);
        return card.Bottom;
    }

    /// <summary>The short list: title, then the artist in the quiet colour, the figure (plays / date) on the right.</summary>
    private int DrawList(Graphics g, int x, int y, int w)
    {
        DrawLabel(g, x, y, w, _listLabel);
        int ry = y + LabelH;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var rect = new Rectangle(x, ry + i * RowH, w, RowH);
            _hit.Add((rect, row));
            if (rect.Bottom < 0 || rect.Top > Height) continue;
            if (ReferenceEquals(row, _hover)) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(new Rectangle(rect.X - 8, rect.Y, rect.Width + 16, rect.Height), 6); g.FillPath(hb, hp); }
            int figW = row.Figure.Length > 0 ? TextRenderer.MeasureText(row.Figure, _fFig, new Size(200, RowH), TextFormatFlags.NoPrefix).Width + 6 : 0;
            int textW = w - figW;
            int titleW = TextRenderer.MeasureText(row.Title, _fRow, new Size(int.MaxValue, RowH), TextFormatFlags.NoPrefix).Width;
            int titleMax = Math.Min(titleW, (int)(textW * 0.62));
            TextRenderer.DrawText(g, row.Title, _fRow, new Rectangle(rect.X, rect.Y, titleMax, RowH), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (row.Sub.Length > 0)
                TextRenderer.DrawText(g, row.Sub, _fSub, new Rectangle(rect.X + titleMax + 10, rect.Y, Math.Max(0, textW - titleMax - 10), RowH), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (figW > 0)
                TextRenderer.DrawText(g, row.Figure, _fFig, new Rectangle(rect.Right - figW, rect.Y, figW, RowH), row.Accent ? Theme.Accent : Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (i < _rows.Count - 1) { using var pen = new Pen(Theme.HairLine); g.DrawLine(pen, rect.X, rect.Bottom - 1, rect.Right, rect.Bottom - 1); }
        }
        return ry + _rows.Count * RowH;
    }

    /// <summary>The header tile for the home page: a house on the accent, at the header's cover size.</summary>
    public static Bitmap HeaderTile(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The same rounded tile every generated cover wears (CoverArt.RenderInto): the header draws its art as-is,
        // so a square fill here showed square corners next to the rounded covers everywhere else.
        float rad = Math.Max(3, size * Theme.TileFrac);
        using (var tile = Theme.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), rad))
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Theme.Blend(Theme.Accent, Color.White, 0.10), Theme.Blend(Theme.Accent, Color.Black, 0.30), Theme.ArtAngle))
            g.FillPath(bg, tile);
        float s = size, cx = s / 2f;
        using var ink = new SolidBrush(Color.FromArgb(235, Theme.OnAccent));
        g.FillPolygon(ink, new[] { new PointF(cx, s * 0.24f), new PointF(s * 0.22f, s * 0.50f), new PointF(s * 0.78f, s * 0.50f) });   // roof
        g.FillRectangle(ink, s * 0.30f, s * 0.50f, s * 0.40f, s * 0.28f);                                                             // walls
        using var hole = new SolidBrush(Theme.Blend(Theme.Accent, Color.Black, 0.30));
        g.FillRectangle(hole, s * 0.44f, s * 0.60f, s * 0.12f, s * 0.18f);                                                            // door
        using (var ip = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), rad))
        using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255))) g.DrawPath(pen, ip);   // the faint inner frame the other tiles have
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var s in _strips) foreach (var t in s.Tiles) t.Tween?.Cancel(); _fLabel.Dispose(); _fTitle.Dispose(); _fSub.Dispose(); _fRow.Dispose(); _fFig.Dispose(); _fCardTitle.Dispose(); }
        base.Dispose(disposing);
    }
}
