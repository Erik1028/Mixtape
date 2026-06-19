using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Apple-Music-style content header: a large generated artwork tile on the left, then a small
/// accent "kicker" (LIBRARY / PLAYLIST), the big title, a "N songs · duration" subtitle, and the
/// primary actions (Add music / Delete) as pill buttons. The buttons are public so the form
/// wires their Click + Enabled.
/// </summary>
internal sealed class HeaderPanel : Panel
{
    public readonly ThemedButton AddButton = new() { Text = "Add music", Width = 132, Primary = true, Pill = true, Glyph = "+", BlockedReason = "No iPod is connected." };
    public readonly ThemedButton DeleteButton = new() { Text = "Delete", Width = 96, Pill = true, Danger = true, BlockedReason = "No iPod is connected." };
    public readonly ThemedButton CoverButton = new() { Text = "Cover Flow", Width = 120, Pill = true };

    /// <summary>The search box, hosted in the header (positioned just left of the action-button stack).</summary>
    public Control? Search { get; set; }
    private int _buttonsLeft, _searchLeft, _buttonsBottom;

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

    // A small status line under the action buttons: warnings (clickable), read-only notes, and transient
    // feedback ("Updated…", "N selected…"). Replaces the old bottom status strip.
    private string _status = "";
    private bool _statusClickable;
    private Rectangle _statusRect = Rectangle.Empty;
    private bool _statusHover;
    /// <summary>Raised when the (clickable) status line is clicked — wired to show the DB warnings.</summary>
    public event Action? StatusClicked;

    public void SetStatus(string text, bool clickable)
    {
        text ??= "";
        if (_status == text && _statusClickable == clickable) return;
        _status = text; _statusClickable = clickable;
        Invalidate();
    }

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
            bool overStatus = _statusClickable && _statusRect.Contains(e.Location);
            if (overArt != _artHover || overStatus != _statusHover)
            {
                _artHover = overArt; _statusHover = overStatus;
                Cursor = (overArt || overStatus) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        };
        MouseLeave += (_, _) => { if (_artHover || _statusHover) { _artHover = _statusHover = false; Cursor = Cursors.Default; Invalidate(); } };
        MouseClick += (_, e) =>
        {
            if (ArtClickable && ArtRect.Contains(e.Location)) ArtClicked?.Invoke();
            else if (_statusClickable && _statusRect.Contains(e.Location)) StatusClicked?.Invoke();
        };
    }

    private Rectangle ArtRect => new(Pad, Pad, ArtSize, ArtSize);

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
        if (disposing) { _artTween?.Cancel(); _art?.Dispose(); _art = null; _artPrev?.Dispose(); _artPrev = null; }
        base.Dispose(disposing);
    }

    private int ArtSize => Math.Max(64, Height - Pad * 2);
    private int TextX => Pad + ArtSize + 18;

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

    /// <summary>Layout A — a right-aligned control cluster: the search box sits directly above one
    /// horizontal row of buttons (Cover Flow / Add / Delete), sharing the same right edge, vertically
    /// centred; the title block keeps the left. Skips hidden buttons. Called on resize + before each paint.</summary>
    public void LayoutButtons()
    {
        const int bh = 30, gap = 10, searchH = 32, gapV = 8;
        var items = new (ThemedButton b, int w)[] { (CoverButton, 124), (AddButton, 132), (DeleteButton, 104) };
        int totalW = 0, n = 0;
        foreach (var (b, w) in items) if (b.Visible) { totalW += w; n++; }
        if (n > 1) totalW += (n - 1) * gap;

        bool hasBtns = n > 0, hasSearch = Search is { Visible: true };
        int clusterH = (hasSearch ? searchH : 0) + (hasSearch && hasBtns ? gapV : 0) + (hasBtns ? bh : 0);
        int top = Math.Max(8, (Height - clusterH) / 2);
        int btnTop = top + (hasSearch ? searchH + gapV : 0);

        int right = Width - Pad, rowLeft = right - totalW, x = rowLeft;
        foreach (var (b, w) in items) if (b.Visible) { b.SetBounds(x, btnTop, w, bh); x += w + gap; }
        _buttonsLeft = hasBtns ? rowLeft : right;
        _buttonsBottom = hasBtns ? btnTop + bh : top;

        if (Search is { Visible: true } search)
        {
            int sw = Math.Max(180, hasBtns ? totalW : 220);   // search spans the button cluster width
            int sx = right - sw;
            search.SetBounds(sx, top, sw, searchH);
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
        var artRect = new Rectangle(Pad, Pad, artSize, artSize);
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
            TextRenderer.DrawText(g, "Change cover", Theme.UiFont(8.5f, FontStyle.Bold), artRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int tx = TextX;
        // Leave room for the search box + action-button stack on the right so the title never runs under them.
        int rightLimit = (Search is { Visible: true } ? _searchLeft : _buttonsLeft) - 16;
        int rightW = Math.Max(80, rightLimit - tx);
        using var kickerFont = Theme.UiFont(8.5f, FontStyle.Bold);
        using var titleFont = Theme.DisplayFont(21f, FontStyle.Bold);
        using var subFont = Theme.UiFont(9.5f);

        // Vertically centre the kicker → title → subtitle block (compact header).
        bool hasKicker = !string.IsNullOrEmpty(_kicker);
        int kh = hasKicker ? TextRenderer.MeasureText(g, _kicker, kickerFont).Height : 0;
        int th = TextRenderer.MeasureText(g, string.IsNullOrEmpty(_title) ? "Ag" : _title, titleFont).Height;
        int subH = TextRenderer.MeasureText(g, "Ag", subFont).Height;
        int gap1 = hasKicker ? 2 : 0;
        int total = kh + gap1 + th + 2 + subH;
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
            using var subBold = Theme.UiFont(9.5f, FontStyle.Bold);
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

        // Status line under the action buttons (right-aligned): warnings / read-only / transient feedback.
        if (!string.IsNullOrEmpty(_status))
        {
            using var statusFont = Theme.UiFont(8.75f, _statusClickable ? FontStyle.Bold : FontStyle.Regular);
            var sz = TextRenderer.MeasureText(g, _status, statusFont);
            // Tuck the status line right-aligned just under the button row (within the control cluster).
            int right = Width - Pad;
            // Width to fit the status in. With a right cluster (search/buttons) keep it within that cluster's
            // width; with NO cluster (e.g. the Device page) _searchLeft sits at the right edge → that gap is 0,
            // so fall back to the full text-area width or the summary clips to "185 son…".
            int avail = _searchLeft < right ? (right - _searchLeft) : (right - TextX);
            int sw = Math.Min(sz.Width + 6, Math.Max(60, avail));
            int sx = right - sw;
            int sy = Math.Min(Height - 6 - sz.Height, _buttonsBottom + 4);
            _statusRect = new Rectangle(sx, sy, sw, sz.Height);
            // Warnings read in a warm amber; everything else stays quiet. Brighten on hover.
            Color col = _statusClickable
                ? Theme.Blend(Theme.Bg, _statusHover ? Color.FromArgb(255, 245, 190, 90) : Color.FromArgb(255, 226, 162, 70), 0.92)
                : Theme.Subtle;
            TextRenderer.DrawText(g, _status, statusFont, _statusRect, col,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else _statusRect = Rectangle.Empty;

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        Theme.CarveCardCorners(g, this, Theme.RadShell, true, true, false, false);   // content card's TOP corners
    }
}
