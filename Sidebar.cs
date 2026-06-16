using System.Drawing.Drawing2D;

namespace iPodCommander;

internal enum SidebarRowKind { Section, Device, AllSongs, Albums, Artists, Videos, Photos, Playlist, LocalMusic }

/// <summary>
/// Apple-Music-style left rail: a "Mixtape" wordmark, then sections (DEVICE / LIBRARY /
/// PLAYLISTS) of clickable rows with a small coloured tile, hover + selection pill, mouse-wheel
/// scrolling, and Refresh / Open-folder buttons pinned at the bottom. Fully owner-painted so it
/// renders identically on screen and via DrawToBitmap.
/// </summary>
internal sealed class Sidebar : Panel
{
    public event Action<SidebarRowKind, object?>? RowActivated;
    public event Action<SidebarRowKind, object?, Point>? RowRightClicked;
    public event Action<Point>? PlaylistAreaRightClicked; // right-click on empty space / the PLAYLISTS header
    public event Action? RefreshClicked;
    public event Action? OpenFolderClicked;
    public event Action? SettingsClicked;
    public event Action<object?>? EjectClicked; // the ⏏ icon on a device row
    public event Action? PlayFileClicked;       // the ▶ icon in the header (play a PC file)

    private sealed class Row
    {
        public SidebarRowKind Kind;
        public string Text = "";
        public object? Tag;
        public bool Active;
        public bool Hint;      // a faint, non-interactive helper line
        public Color Tile;
        public Bitmap? Icon;   // real mini cover art, when available
    }

    private readonly List<Row> _rows = new();
    private readonly List<(Rectangle Rect, Row Row)> _hit = new();
    private readonly List<(Rectangle Rect, Row Row)> _ejectHit = new(); // ⏏ sub-regions on device rows
    private Row? _hover;
    private Row? _ejectHover;
    private int _scroll;
    private int _contentH; // total laid-out row height, captured each paint; used to clamp scrolling
    private Bitmap? _logo;

    private readonly ThemedButton _refresh = new() { Text = "Refresh", Pill = true, Height = 30 };
    private readonly ThemedButton _openFolder = new() { Text = "Open folder", Pill = true, Height = 30 };
    private readonly ThemedButton _settings = new() { Text = "⚙", Width = 30, Height = 28, Ghost = true, Font = Theme.UiFont(13f) };
    private readonly ThemedButton _playFile = new() { Text = "▶", Width = 30, Height = 28, Ghost = true, Font = Theme.UiFont(10f) };
    private readonly ToolTip _tip = new();

    private const int HeaderH = 60, SectionH = 30, ItemH = 34, FooterH = 56, Pad = 12;

    public Sidebar()
    {
        BackColor = Theme.SidebarBg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Controls.Add(_refresh);
        Controls.Add(_openFolder);
        Controls.Add(_settings);
        Controls.Add(_playFile);
        _refresh.Click += (_, _) => RefreshClicked?.Invoke();
        _openFolder.Click += (_, _) => OpenFolderClicked?.Invoke();
        _settings.Click += (_, _) => SettingsClicked?.Invoke();
        _playFile.Click += (_, _) => PlayFileClicked?.Invoke();
        _tip.SetToolTip(_playFile, "Play an audio file from your PC");
        _tip.SetToolTip(_settings, "Settings");

        try { if (Environment.ProcessPath is string p) _logo = System.Drawing.Icon.ExtractAssociatedIcon(p)?.ToBitmap(); } catch { }

        MouseMove += (_, e) =>
        {
            var ej = EjectHitTest(e.Location);
            Cursor = ej is not null ? Cursors.Hand : Cursors.Default;
            var r = HitTest(e.Location);
            if (!ReferenceEquals(r, _hover) || !ReferenceEquals(ej, _ejectHover)) { _hover = r; _ejectHover = ej; Invalidate(); }
        };
        MouseLeave += (_, _) => { _hover = null; _ejectHover = null; Invalidate(); };
        MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && EjectHitTest(e.Location) is { } ejectRow) { EjectClicked?.Invoke(ejectRow.Tag); return; }
            var r = HitTest(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                // A real row → its own menu; empty space or a section header → the playlist-area menu.
                if (r is not null && r.Kind != SidebarRowKind.Section) RowRightClicked?.Invoke(r.Kind, r.Tag, PointToScreen(e.Location));
                else PlaylistAreaRightClicked?.Invoke(PointToScreen(e.Location));
                return;
            }
            if (r is null || r.Kind == SidebarRowKind.Section) return;
            RowActivated?.Invoke(r.Kind, r.Tag);
        };
        MouseWheel += (_, e) => ClampScroll(_scroll - Math.Sign(e.Delta) * 40);
    }

    /// <summary>Clamp the scroll offset to [0, content − visible] so the list can't scroll past its end into empty space.</summary>
    private void ClampScroll(int value)
    {
        int visible = Math.Max(0, Height - HeaderH - FooterH);
        int max = Math.Max(0, _contentH - visible);
        int v = Math.Clamp(value, 0, max);
        if (v != _scroll) { _scroll = v; Invalidate(); }
    }

    // ---- content building ----
    public void Begin() => _rows.Clear();
    public void AddSection(string text) => _rows.Add(new Row { Kind = SidebarRowKind.Section, Text = text });
    public void AddItem(SidebarRowKind kind, string text, object? tag, bool active) =>
        _rows.Add(new Row { Kind = kind, Text = text, Tag = tag, Active = active, Tile = TileColor(kind, text) });
    public void AddHint(string text) => _rows.Add(new Row { Kind = SidebarRowKind.Section, Text = text, Hint = true });
    public void End() { _scroll = 0; Invalidate(); }

    /// <summary>Attach a loaded mini cover to the row whose Tag matches by reference.</summary>
    public void SetIcon(object tag, Bitmap icon)
    {
        foreach (var r in _rows)
            if (r.Tag is not null && ReferenceEquals(r.Tag, tag)) { r.Icon = icon; Invalidate(); return; }
    }

    private static Color TileColor(SidebarRowKind kind, string text) => kind switch
    {
        SidebarRowKind.Device or SidebarRowKind.AllSongs or SidebarRowKind.Albums or SidebarRowKind.Artists or SidebarRowKind.Videos or SidebarRowKind.Photos => Theme.Accent,
        _ => Theme.HsvToColor(150 + Theme.StableHash(text) % 150, 0.55, 0.70),
    };

    /// <summary>Draws a crisp, centred vector icon for the row kind (replaces fuzzy Unicode glyphs).</summary>
    private static void DrawRowGlyph(Graphics g, Rectangle t, SidebarRowKind kind, Color c)
    {
        float s = t.Width, x = t.X, y = t.Y;
        float stroke = Math.Max(1.4f, s * 0.09f);
        using var br = new SolidBrush(c);
        using var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        switch (kind)
        {
            case SidebarRowKind.Videos: // play triangle
            {
                var tri = new[] { new PointF(x + s * 0.36f, y + s * 0.27f), new PointF(x + s * 0.36f, y + s * 0.73f), new PointF(x + s * 0.73f, y + s * 0.50f) };
                using var gp = new GraphicsPath();
                gp.AddPolygon(tri);
                g.FillPath(br, gp);
                break;
            }
            case SidebarRowKind.Photos: // framed landscape (sun + mountain)
            {
                var frame = new RectangleF(x + s * 0.20f, y + s * 0.24f, s * 0.60f, s * 0.52f);
                using var fp = Theme.RoundedRect(frame, s * 0.11f);
                var savedClip = g.Clip;
                g.DrawPath(pen, fp);
                g.SetClip(fp);
                g.FillEllipse(br, x + s * 0.28f, y + s * 0.31f, s * 0.13f, s * 0.13f);
                var mtn = new[] { new PointF(x + s * 0.18f, y + s * 0.78f), new PointF(x + s * 0.40f, y + s * 0.52f), new PointF(x + s * 0.55f, y + s * 0.64f), new PointF(x + s * 0.82f, y + s * 0.40f), new PointF(x + s * 0.82f, y + s * 0.78f) };
                g.FillPolygon(br, mtn);
                g.Clip = savedClip;
                break;
            }
            case SidebarRowKind.Playlist: // list with a note bullet
            {
                float bx = x + s * 0.24f, bw = s * 0.40f;
                for (int i = 0; i < 3; i++)
                    using (var bp = Theme.RoundedRect(new RectangleF(bx, y + s * (0.29f + 0.165f * i), bw, stroke), stroke / 2))
                        g.FillPath(br, bp);
                g.FillEllipse(br, x + s * 0.66f, y + s * 0.27f, s * 0.16f, s * 0.16f);
                break;
            }
            case SidebarRowKind.Albums: // vinyl disc
            {
                g.DrawEllipse(pen, x + s * 0.20f, y + s * 0.20f, s * 0.60f, s * 0.60f);
                g.FillEllipse(br, x + s * 0.44f, y + s * 0.44f, s * 0.12f, s * 0.12f);
                break;
            }
            case SidebarRowKind.Artists: // person silhouette
            {
                g.FillEllipse(br, x + s * 0.37f, y + s * 0.20f, s * 0.26f, s * 0.26f);
                var sh = new[] { new PointF(x + s * 0.26f, y + s * 0.82f), new PointF(x + s * 0.34f, y + s * 0.54f), new PointF(x + s * 0.66f, y + s * 0.54f), new PointF(x + s * 0.74f, y + s * 0.82f) };
                g.FillPolygon(br, sh);
                break;
            }
            case SidebarRowKind.Device: // iPod — rounded body, screen, click wheel
            {
                using (var bp = Theme.RoundedRect(new RectangleF(x + s * 0.27f, y + s * 0.10f, s * 0.46f, s * 0.80f), s * 0.12f))
                    g.DrawPath(pen, bp);
                using (var sp = Theme.RoundedRect(new RectangleF(x + s * 0.34f, y + s * 0.17f, s * 0.32f, s * 0.22f), s * 0.04f))
                    g.FillPath(br, sp);                                  // screen
                float wd = s * 0.30f, wx = x + (s - wd) / 2f, wy = y + s * 0.50f;
                g.DrawEllipse(pen, wx, wy, wd, wd);                      // click wheel
                g.FillEllipse(br, wx + wd * 0.36f, wy + wd * 0.36f, wd * 0.28f, wd * 0.28f); // centre button
                break;
            }
            case SidebarRowKind.LocalMusic: // laptop (music on this PC)
            {
                using (var sp = Theme.RoundedRect(new RectangleF(x + s * 0.22f, y + s * 0.20f, s * 0.56f, s * 0.40f), s * 0.06f))
                    g.DrawPath(pen, sp);                                  // screen
                g.FillRectangle(br, x + s * 0.12f, y + s * 0.64f, s * 0.76f, s * 0.10f); // base
                g.FillEllipse(br, x + s * 0.41f, y + s * 0.42f, s * 0.11f, s * 0.09f);    // little note head
                g.FillRectangle(br, x + s * 0.50f, y + s * 0.30f, Math.Max(1.2f, s * 0.045f), s * 0.16f); // stem
                break;
            }
            default: // AllSongs → eighth note
            {
                float headW = s * 0.30f, headH = s * 0.23f;
                float hx = x + s * 0.28f, hy = y + s * 0.55f;
                g.FillEllipse(br, hx, hy, headW, headH);
                float stemX = hx + headW - stroke;
                float stemTop = y + s * 0.26f;
                g.FillRectangle(br, stemX, stemTop, stroke, hy + headH * 0.5f - stemTop);
                var flag = new[] { new PointF(stemX + stroke, stemTop), new PointF(stemX + s * 0.20f, stemTop + s * 0.10f), new PointF(stemX + stroke, stemTop + s * 0.22f) };
                g.FillPolygon(br, flag);
                break;
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Two equal-width pill buttons side by side, filling the rail with a small gap, centred in the footer.
        const int gap = 8;
        int bw = Math.Max(40, (Width - Pad * 2 - gap) / 2);
        int by = Height - FooterH + (FooterH - _refresh.Height) / 2;
        _refresh.Width = _openFolder.Width = bw;
        _refresh.Location = new Point(Pad, by);
        _openFolder.Location = new Point(Pad + bw + gap, by);
        _settings.Location = new Point(Width - Pad - _settings.Width, 15);
        _playFile.Location = new Point(_settings.Left - 4 - _playFile.Width, 15);
        ClampScroll(_scroll); // a shorter window mustn't leave the list stranded past its new bottom
        Invalidate();
    }

    private Row? HitTest(Point p)
    {
        foreach (var (rect, row) in _hit) if (rect.Contains(p)) return row;
        return null;
    }

    private Row? EjectHitTest(Point p)
    {
        foreach (var (rect, row) in _ejectHit) if (rect.Contains(p)) return row;
        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SidebarBg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // --- wordmark header ---
        if (_logo != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(_logo, new Rectangle(Pad, 15, 28, 28)); }
        TextRenderer.DrawText(g, "Mixtape", Theme.DisplayFont(13f, FontStyle.Bold),
            new Rectangle(Pad + 34, 14, Width - Pad - 40, 30), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // --- rows (scrollable region) ---
        _hit.Clear();
        _ejectHit.Clear();
        var clip = new Rectangle(0, HeaderH, Width, Height - HeaderH - FooterH);
        g.SetClip(clip);
        int y = HeaderH - _scroll;
        bool anyDrawn = false;
        foreach (var row in _rows)
        {
            if (row.Hint)
            {
                if (y + ItemH > clip.Top && y < clip.Bottom)
                    TextRenderer.DrawText(g, row.Text, Theme.UiFont(8.5f, FontStyle.Italic),
                        new Rectangle(Pad + 8, y, Width - Pad * 2 - 8, ItemH), Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis);
                y += ItemH;
                anyDrawn = true;
                continue;
            }
            if (row.Kind == SidebarRowKind.Section)
            {
                if (anyDrawn) y += 10; // breathing room above each group after the first
                if (y + SectionH > clip.Top)
                    TextRenderer.DrawText(g, row.Text, Theme.UiFont(8f, FontStyle.Bold),
                        new Rectangle(Pad + 4, y, Width - Pad * 2, SectionH), Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.Bottom);
                y += SectionH;
                anyDrawn = true;
                continue;
            }

            var rowRect = new Rectangle(0, y, Width, ItemH);
            _hit.Add((rowRect, row));
            if (y + ItemH > clip.Top && y < clip.Bottom)
            {
                var pill = new Rectangle(Pad - 2, y + 2, Width - (Pad - 2) * 2, ItemH - 4);
                bool hover = ReferenceEquals(row, _hover);
                if (row.Active || hover)
                {
                    // Active = translucent teal wash (a tinted pill, not a solid block);
                    // hover = a faint grey lift. Both rounded, à la Apple Music.
                    Color fill = row.Active
                        ? Color.FromArgb(48, Theme.Accent)
                        : Theme.Blend(Theme.SidebarBg, Color.White, 0.06);
                    using var pb = new SolidBrush(fill);
                    using var pp = Theme.RoundedRect(pill, Theme.RadControl);
                    g.FillPath(pb, pp);
                }

                // icon: real mini cover when available, else a coloured tile with a white glyph
                int ts = 18;
                var tile = new Rectangle(pill.X + 10, y + (ItemH - ts) / 2, ts, ts);
                if (row.Icon != null)
                {
                    // Round-clip + high-quality downscale so a real/chosen cover matches the rounded tiles
                    // instead of showing as a harsh, muddy square.
                    var prevI = g.InterpolationMode; var prevP = g.PixelOffsetMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var ip = Theme.RoundedRect(tile, Theme.RadTileSmall))
                    {
                        var saved = g.Clip;
                        g.SetClip(ip, CombineMode.Intersect);
                        g.DrawImage(row.Icon, tile);
                        g.Clip = saved;
                        using var bp = new Pen(Color.FromArgb(70, 0, 0, 0)); // subtle edge so light covers don't bleed into the rail
                        using var ip2 = Theme.RoundedRect(new RectangleF(tile.X + 0.5f, tile.Y + 0.5f, tile.Width - 1, tile.Height - 1), Theme.RadTileSmall);
                        g.DrawPath(bp, ip2);
                    }
                    g.InterpolationMode = prevI; g.PixelOffsetMode = prevP;
                }
                else
                {
                    using (var tb = new SolidBrush(row.Tile))
                    using (var tp = Theme.RoundedRect(tile, Theme.RadTileSmall))
                        g.FillPath(tb, tp);
                    DrawRowGlyph(g, tile, row.Kind, Color.FromArgb(244, 255, 255, 255));
                }

                // A device row gets an ⏏ eject button on the right (iTunes-style); reserve space for it.
                int rightInset = 16;
                if (row.Kind == SidebarRowKind.Device)
                {
                    const int ew = 30;
                    var ejectRect = new Rectangle(pill.Right - ew, y, ew, ItemH);
                    _ejectHit.Add((ejectRect, row));
                    Color ec = ReferenceEquals(row, _ejectHover) ? Theme.AccentBright : (row.Active ? Color.FromArgb(220, 255, 255, 255) : Theme.Faint);
                    float ex = ejectRect.X + ejectRect.Width / 2f, ey = y + ItemH / 2f;
                    using var eb = new SolidBrush(ec);
                    g.FillPolygon(eb, new[] { new PointF(ex - 5, ey - 1), new PointF(ex + 5, ey - 1), new PointF(ex, ey - 7) }); // ▲
                    g.FillRectangle(eb, ex - 5, ey + 2.5f, 10, 2.2f);                                                            // ▁
                    rightInset = ew + 6;
                }

                var textRect = new Rectangle(tile.Right + 10, y, pill.Right - tile.Right - rightInset, ItemH);
                Color tc = row.Active ? Color.White : Theme.TextCol;
                TextRenderer.DrawText(g, row.Text, Theme.UiFont(9.5f, row.Active ? FontStyle.Bold : FontStyle.Regular),
                    textRect, tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            y += ItemH;
            anyDrawn = true;
        }
        _contentH = y + _scroll - HeaderH; // absolute height of all rows, independent of the current scroll
        g.ResetClip();

        // hairline above the footer, inset so it doesn't run into the card's rounded corners
        using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, Pad, Height - FooterH, Width - Pad, Height - FooterH);
        // (No right-edge seam — the sidebar's darker background already separates it from the content;
        //  a hard vertical line there read as an out-of-place divider.)
    }
}
