using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// Apple-Music-style content header: a large generated artwork tile on the left, then a small
/// accent "kicker" (LIBRARY / PLAYLIST), the big title, a "N songs · duration" subtitle, and the
/// primary actions (Add music / Delete) as pill buttons. The buttons are public so the form
/// wires their Click + Enabled.
/// </summary>
internal sealed class HeaderPanel : Panel
{
    public readonly ThemedButton AddButton = new() { Text = Loc.T("Add music"), Width = 132, Primary = true, Pill = true, Glyph = "+", Icon = ThemedButton.Ico.Add, BlockedReason = Loc.T("No iPod is connected.") };
    public readonly ThemedButton DeleteButton = new() { Text = Loc.T("Delete"), Width = 96, Pill = true, Danger = true, Icon = ThemedButton.Ico.Trash, BlockedReason = Loc.T("No iPod is connected.") };
    public readonly ThemedButton CoverButton = new() { Text = Loc.T("Cover Flow"), Width = 120, Pill = true, Icon = ThemedButton.Ico.CoverFlow };

    /// <summary>The search box, hosted in the header (positioned just left of the action-button stack).</summary>
    public Control? Search { get; set; }
    /// <summary>An optional size slider (the Photos view), hosted in the search row above the buttons.</summary>
    public Control? SizeSlider { get; set; }
    private int _buttonsLeft, _searchLeft, _buttonsBottom;

    // The window control buttons (mini-player · minimize · maximize · close) now live in the header's top-right
    // (there's no separate title strip). The header lays them out + drives window drag from its empty area.
    private Control[] _winButtons = Array.Empty<Control>();
    private const int WinBh = 28, WinBw = 38, WinTop = 12;   // window-button cell size + top inset
    private int WinZoneH => _winButtons.Length > 0 ? WinTop + WinBh : 0;

    public void SetWindowButtons(params Control[] buttons)
    {
        _winButtons = buttons;
        foreach (var b in buttons) Controls.Add(b);
        LayoutButtons();
    }

    /// <summary>Raised when the artwork tile is clicked (only when <see cref="ArtClickable"/>) — used to pick a cover.</summary>
    public event Action? ArtClicked;
    /// <summary>When true the artwork shows a hand cursor + "Change cover" hint on hover and raises <see cref="ArtClicked"/>.</summary>
    public bool ArtClickable { get; set; }

    private string _kicker = "";
    private string _title = "";
    private string _subtitle = "";
    private int _seed;
    private Bitmap? _art;
    private Bitmap? _artPrev;     // outgoing snapshot held during a cross-fade
    private float _artFade = 1f;  // 0 = art just changed (show outgoing), 1 = settled (show current)
    private Tween? _artTween;
    private bool _artHover;

    // A small status line under the action buttons: read-only notes + transient feedback ("Updated…",
    // "N selected…"). The DB warning moved OUT of here into its own amber badge (see _badge below).
    private string _status = "";
    private bool _statusClickable;   // retained for SetStatus API compat; this line no longer acts clickable
    private Rectangle _statusRect = Rectangle.Empty;

    // The DB-warning badge: an amber pill tucked under the subtitle (header's left text column), clickable to
    // open the warnings list. Kept separate from _status so an action message never wipes the warning.
    private string _badge = "";
    private bool _badgeClickable;
    private Rectangle _badgeRect = Rectangle.Empty;
    private bool _badgeHover;
    /// <summary>Raised when the (clickable) warning badge is clicked — wired to show the DB warnings.</summary>
    public event Action? StatusClicked;

    public void SetStatus(string text, bool clickable)
    {
        text ??= "";
        if (_status == text && _statusClickable == clickable) return;
        _status = text; _statusClickable = clickable;
        Invalidate();
    }

    /// <summary>Set the amber warning badge under the subtitle ("" hides it). Clickable opens the warnings list.</summary>
    public void SetBadge(string text, bool clickable)
    {
        text ??= "";
        if (_badge == text && _badgeClickable == clickable) return;
        _badge = text; _badgeClickable = clickable;
        Invalidate();
    }

    // Cached fonts (Theme.UiFont/DisplayFont allocate a fresh GDI Font per call; the header repaints on hover).
    private readonly Font _fKicker = Theme.UiFont(8.5f, FontStyle.Bold);   // kicker · warning badge · "Change cover"
    private readonly Font _fTitleFont = Theme.DisplayFont(21f, FontStyle.Bold);
    private readonly Font _fSub = Theme.UiFont(9.5f);
    private readonly Font _fSubBold = Theme.UiFont(9.5f, FontStyle.Bold);
    private readonly Font _fStatus = Theme.UiFont(8.75f);

    private const int Pad = 18;

    public HeaderPanel()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(AddButton);
        Controls.Add(DeleteButton);
        Controls.Add(CoverButton);

        MouseMove += (_, e) =>
        {
            bool overArt = ArtClickable && ArtRect.Contains(e.Location);
            bool overBadge = _badgeClickable && _badgeRect.Contains(e.Location);
            if (overArt != _artHover || overBadge != _badgeHover)
            {
                _artHover = overArt; _badgeHover = overBadge;
                Cursor = (overArt || overBadge) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        };
        MouseLeave += (_, _) => { if (_artHover || _badgeHover) { _artHover = _badgeHover = false; Cursor = Cursors.Default; Invalidate(); } };
        MouseClick += (_, e) =>
        {
            if (ArtClickable && ArtRect.Contains(e.Location)) ArtClicked?.Invoke();
            else if (_badgeClickable && _badgeRect.Contains(e.Location)) StatusClicked?.Invoke();
        };
        // The header doubles as the title bar now: drag its empty area to move the window (child controls and the
        // clickable art/badge keep their own behaviour), double-click to maximize/restore.
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (ArtClickable && ArtRect.Contains(e.Location)) return;
            if (_badgeClickable && _badgeRect.Contains(e.Location)) return;
            StartWindowDrag();
        };
        MouseDoubleClick += (_, e) =>
        {
            if ((ArtClickable && ArtRect.Contains(e.Location)) || (_badgeClickable && _badgeRect.Contains(e.Location))) return;
            if (FindForm() is { } f) f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        };
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private void StartWindowDrag()
    {
        // WM_NCLBUTTONDOWN + HTCAPTION → native move (with aero-snap), exactly like dragging a real title bar.
        try { if (FindForm() is { } f) { ReleaseCapture(); SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } } catch { }
    }

    private Rectangle ArtRect => new(Pad, (Height - ArtSize) / 2, ArtSize, ArtSize);

    public void SetInfo(string kicker, string title, string subtitle, int seed)
    {
        _kicker = kicker; _title = title; _subtitle = subtitle; _seed = seed;
        SetArt(null); // disposes any prior art
    }

    /// <summary>
    /// Show real cover art instead of the generated gradient (null reverts to gradient). The header
    /// takes a PRIVATE copy and owns it — so callers may pass a cache-owned bitmap (album art, a
    /// CoverArt tile) or a fresh one freely, and the previous header art is disposed here (no leak).
    /// </summary>
    public void SetArt(Bitmap? art)
    {
        var newCopy = art is null ? null : new Bitmap(art);
        var old = _art;
        if (ReferenceEquals(old, newCopy)) return; // both null → nothing to do

        // Capture whatever is on screen right now, then cross-dissolve to the new art (Apple-style).
        _artTween?.Cancel();
        _artPrev?.Dispose();
        _artPrev = SnapshotTile();
        _art = newCopy;
        if (!ReferenceEquals(old, _art)) old?.Dispose();
        _artFade = 0f;
        _artTween = Anim.Run(220,
            v => { _artFade = (float)v; if (!IsDisposed) InvalidateArt(); },
            () => { _artTween = null; _artPrev?.Dispose(); _artPrev = null; _artFade = 1f; if (!IsDisposed) InvalidateArt(); },
            Easings.OutCubic);
        InvalidateArt();
    }

    /// <summary>A square snapshot of the tile currently displayed (real art or the generated gradient).</summary>
    private Bitmap SnapshotTile()
    {
        int sz = Math.Max(8, ArtSize);
        var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_art ?? Theme.MakeArt(sz, _seed), 0, 0, sz, sz);
        return bmp;
    }

    private void InvalidateArt() => Invalidate(new Rectangle(Pad - 2, Pad - 2, ArtSize + 8, ArtSize + 12));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _artTween?.Cancel(); _art?.Dispose(); _art = null; _artPrev?.Dispose(); _artPrev = null; _fKicker.Dispose(); _fTitleFont.Dispose(); _fSub.Dispose(); _fSubBold.Dispose(); _fStatus.Dispose(); }
        base.Dispose(disposing);
    }

    private int ArtSize => Math.Clamp(Height - Pad * 2, 64, 96);
    private int TextX => Pad + ArtSize + 18;

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

    /// <summary>Layout A — a right-aligned control cluster: the search box sits directly above one
    /// horizontal row of buttons (Cover Flow / Add / Delete), sharing the same right edge, vertically
    /// centred; the title block keeps the left. Skips hidden buttons. Called on resize + before each paint.</summary>
    private const int TitleMinW = 96;   // the title block always keeps at least this much width (right cluster won't cross it)

    public void LayoutButtons()
    {
        const int bh = 30, gap = 10, searchH = 32, gapV = 8, cbw = 36;   // cbw = compact icon-button width

        // Window buttons first: a right-aligned row in the top-right corner (mini · min · max · close).
        if (_winButtons.Length > 0)
        {
            int wx = Width - Pad - _winButtons.Length * WinBw;
            foreach (var b in _winButtons) { b.SetBounds(wx, WinTop, WinBw, WinBh); wx += WinBw; }
        }

        var items = new (ThemedButton b, int w)[] { (CoverButton, 124), (AddButton, 132), (DeleteButton, 104) };
        int right = Width - Pad;
        int minClusterLeft = TextX + TitleMinW + 16;   // the right cluster (search + buttons) must not reach left of this

        int fullW = 0, n = 0;
        foreach (var (b, w) in items) if (b.Visible) { fullW += w; n++; }
        if (n > 1) fullW += (n - 1) * gap;
        bool hasBtns = n > 0;
        // Collapse the action buttons to icon-only pills when the full text row would overlap the title.
        bool compact = hasBtns && (right - fullW) < minClusterLeft;
        int btnRowW = compact ? n * cbw + Math.Max(0, n - 1) * gap : fullW;
        foreach (var (b, _) in items) b.CompactIcon = compact;

        bool hasSearch = Search is { Visible: true }, hasSlider = !hasSearch && SizeSlider is { Visible: true };
        bool topRow = hasSearch || hasSlider;
        int clusterH = (topRow ? searchH : 0) + (topRow && hasBtns ? gapV : 0) + (hasBtns ? bh : 0);
        // Sit the search/action cluster BELOW the window-button row (with a gap), instead of vertically centred —
        // so the search bar + buttons are lower and clear of the window controls, with breathing room up top.
        int top = WinZoneH > 0 ? WinZoneH + 10 : Math.Max(12, (Height - clusterH) / 2);
        int btnTop = top + (topRow ? searchH + gapV : 0);

        int rowLeft = right - btnRowW, x = rowLeft;
        foreach (var (b, w) in items) if (b.Visible) { int bw = compact ? cbw : w; b.SetBounds(x, btnTop, bw, bh); x += bw + gap; }
        _buttonsLeft = hasBtns ? rowLeft : right;
        _buttonsBottom = hasBtns ? btnTop + bh : top;

        int maxClusterW = Math.Max(60, right - minClusterLeft);   // widest the search/slider may be without crossing the title
        if (Search is { Visible: true } search)
        {
            int sw = Math.Min(Math.Max(btnRowW, hasBtns ? 200 : 220), maxClusterW);   // align over the buttons; shrink to fit; never cross the title
            int sx = right - sw;
            search.SetBounds(sx, top, sw, searchH);
            _searchLeft = sx;
        }
        else if (hasSlider && SizeSlider is { } slider)
        {
            int sw = Math.Min(Math.Max(btnRowW, 200), maxClusterW);
            int sx = right - sw;
            slider.SetBounds(sx, top + (searchH - slider.Height) / 2, sw, slider.Height);
            _searchLeft = sx;
        }
        else _searchLeft = _buttonsLeft;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        LayoutButtons(); // keep button positions current (visibility changes per view without a resize)
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int artSize = ArtSize;
        var artRect = new Rectangle(Pad, (Height - artSize) / 2, artSize, artSize);
        // soft shadow under the art
        using (var sh = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
        using (var sp = Theme.RoundedRect(new RectangleF(artRect.X + 2, artRect.Y + 4, artSize, artSize), artSize * Theme.TileFrac))
            g.FillPath(sh, sp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var artNow = _art ?? Theme.MakeArt(artSize, _seed);
        if (_artPrev is not null && _artFade < 1f)
        {
            g.DrawImage(_artPrev, artRect);                          // outgoing holds its place
            Theme.DrawImageAlpha(g, artNow, artRect, _artFade);     // incoming dissolves in over it
        }
        else g.DrawImage(artNow, artRect);
        if (ArtClickable && _artHover)
        {
            using (var ov = new SolidBrush(Color.FromArgb(125, 0, 0, 0)))
            using (var op = Theme.RoundedRect(artRect, artSize * Theme.TileFrac))
                g.FillPath(ov, op);
            TextRenderer.DrawText(g, "Change cover", _fKicker, artRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int tx = TextX;
        // Leave room for the search box + action-button stack on the right so the title never runs under them.
        int rightLimit = (Search is { Visible: true } ? _searchLeft : _buttonsLeft) - 16;
        int rightW = Math.Max(80, rightLimit - tx);
        var kickerFont = _fKicker;
        var titleFont = _fTitleFont;
        var subFont = _fSub;
        var badgeFont = _fKicker;

        // Vertically centre the kicker → title → subtitle (→ warning badge) block (compact header).
        bool hasKicker = !string.IsNullOrEmpty(_kicker);
        bool hasBadge = !string.IsNullOrEmpty(_badge);
        int kh = hasKicker ? TextRenderer.MeasureText(g, _kicker, kickerFont).Height : 0;
        int th = TextRenderer.MeasureText(g, string.IsNullOrEmpty(_title) ? "Ag" : _title, titleFont).Height;
        int subH = TextRenderer.MeasureText(g, "Ag", subFont).Height;
        Size badgeText = hasBadge ? TextRenderer.MeasureText(g, _badge, badgeFont) : Size.Empty;
        const int chipPadX = 9, chipPadY = 3;
        int badgeH = hasBadge ? badgeText.Height + chipPadY * 2 : 0;
        int gap1 = hasKicker ? 2 : 0;
        int total = kh + gap1 + th + 2 + subH + (hasBadge ? 6 + badgeH : 0);
        int ty = Math.Max(Pad, (Height - total) / 2);

        if (hasKicker)
        {
            // Calmer accent for the static eyebrow (bright accent is reserved for selection); tucked to the title.
            TextRenderer.DrawText(g, _kicker, kickerFont,
                new Rectangle(tx, ty, rightW, kh), Theme.Blend(Theme.Bg, Theme.Accent, 0.85), TextFormatFlags.Left | TextFormatFlags.Top);
            ty += kh + gap1;
        }

        TextRenderer.DrawText(g, _title, titleFont,
            new Rectangle(tx, ty, rightW, th), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        ty += th + 2;

        // "185 songs · 11 hr" → the leading count owns a bold/bright run; the "· 11 hr" tail stays quiet.
        int dot = _subtitle.IndexOf('·');
        if (dot > 0)
        {
            string head = _subtitle.Substring(0, dot).TrimEnd();
            string tail = _subtitle.Substring(dot);
            var subBold = _fSubBold;
            const TextFormatFlags np = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, head, subBold, new Rectangle(tx, ty, rightW, subH), Theme.TextCol, np);
            int hw = TextRenderer.MeasureText(g, head, subBold, new Size(rightW, subH), np).Width;
            TextRenderer.DrawText(g, "  " + tail, subFont, new Rectangle(tx + hw, ty, rightW - hw, subH), Theme.Subtle, np | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(g, _subtitle, subFont,
                new Rectangle(tx, ty, rightW, subH), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }

        // The DB-warning badge — a small amber pill right under the subtitle (clickable → opens the warnings
        // list). Living here (left column) instead of crammed under the buttons reads as an intentional status.
        if (hasBadge)
        {
            ty += subH + 6;
            var chip = new Rectangle(tx, ty, badgeText.Width + chipPadX * 2, badgeH);
            Color amber = Color.FromArgb(255, 226, 162, 70);
            Color fill = _badgeClickable ? Color.FromArgb(_badgeHover ? 56 : 40, amber) : Color.FromArgb(26, 255, 255, 255);
            using (var bb = new SolidBrush(fill))
            using (var bpath = Theme.RoundedRect(chip, badgeH / 2f))
                g.FillPath(bb, bpath);
            if (_badgeClickable)
                using (var bpen = new Pen(Color.FromArgb(_badgeHover ? 150 : 105, amber)))
                using (var bpath2 = Theme.RoundedRect(new RectangleF(chip.X + 0.5f, chip.Y + 0.5f, chip.Width - 1, chip.Height - 1), (badgeH - 1) / 2f))
                    g.DrawPath(bpen, bpath2);
            Color bcol = _badgeClickable
                ? Theme.Blend(Theme.Bg, _badgeHover ? Color.FromArgb(255, 245, 195, 110) : amber, 0.95)
                : Theme.Subtle;
            TextRenderer.DrawText(g, _badge, badgeFont, chip, bcol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            _badgeRect = chip;
        }
        else _badgeRect = Rectangle.Empty;

        // Read-only note / transient feedback under the action buttons (right-aligned, quiet). The DB warning
        // is no longer shown here — it has its own amber badge under the subtitle (above).
        if (!string.IsNullOrEmpty(_status))
        {
            var statusFont = _fStatus;
            var sz = TextRenderer.MeasureText(g, _status, statusFont);
            int right = Width - Pad;
            // With a right cluster (search/buttons) keep within that width; with none (Device page) _searchLeft
            // sits at the right edge so fall back to the full text-area width.
            int avail = _searchLeft < right ? (right - _searchLeft) : (right - TextX);
            int sw = Math.Min(sz.Width + 6, Math.Max(60, avail));
            int sx = right - sw;
            int sy = Math.Min(Height - 6 - sz.Height, _buttonsBottom + 4);
            TextRenderer.DrawText(g, _status, statusFont, new Rectangle(sx, sy, sw, sz.Height), Theme.Subtle,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        _statusRect = Rectangle.Empty;

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        Theme.CarveCardCorners(g, this, Theme.RadShell, true, true, false, false);   // content card's TOP corners
    }
}
