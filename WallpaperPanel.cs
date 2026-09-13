using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The shell root: paints the themed gradient "wallpaper" + soft shadows behind the floating cards,
/// and reserves a top caption strip (the app's own title bar). Near the window edges and across the
/// caption strip it reports HTTRANSPARENT so the parent Form's WndProc can do native resize / drag /
/// snap (we removed the OS title bar). Window control buttons are children and intercept their own area.
///
/// The wallpaper (with the card shadows baked in) is rendered once into <see cref="Wallpaper"/>; the
/// floating cards sample that bitmap to paint ANTI-ALIASED rounded corners (see Theme.CarveCardCorners),
/// which a hard <see cref="Region"/> clip cannot do. Because the cards sample the SAME bitmap this panel
/// draws, a carved corner is pixel-identical to the gap around it — no seam, no square shadow edge.
/// </summary>
internal sealed class WallpaperPanel : Panel
{
    public int CaptionHeight = 36;
    /// <summary>Paint the logo + wordmark in the caption strip (off when the deck bar paints its own).</summary>
    public bool ShowWordmark = true;
    public int ResizeBorder = 6;
    private Bitmap? _wall;

    public WallpaperPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    /// <summary>The rendered wallpaper + baked card shadows at the current size. Built lazily so it is always
    /// ready when a card asks for it during its own paint.</summary>
    public Bitmap? Wallpaper { get { EnsureWall(); return _wall; } }

    /// <summary>Drop the cached wallpaper so it re-renders (after a theme/accent change or a card move).</summary>
    public void InvalidateWallpaper() { _wall?.Dispose(); _wall = null; Invalidate(); }

    private void EnsureWall()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        if (_wall is { } b && b.Width == w && b.Height == h) return;
        _wall?.Dispose();
        _wall = new Bitmap(w, h);
        using var g = Graphics.FromImage(_wall);
        Theme.PaintWallpaper(g, new Rectangle(0, 0, w, h));
        // Bake the cards' drop-shadows in, so the corner-carving samples wallpaper+shadow as one image.
        foreach (Control c in Controls)
            if (c.Visible && c is not WindowButton && c is not ThemedButton && c is not NowPlayingBar) Theme.PaintCardShadow(g, c.Bounds, Theme.RadShell);   // only the cards cast shadows — not the strip's buttons, not the deck
    }

    /// <summary>The app icon, for the title strip's wordmark.</summary>
    public Bitmap? Logo { get; set; }
    private readonly Font _fWordmark = Theme.DisplayFont(Theme.SzDisplay, FontStyle.Bold);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        EnsureWall();
        var g = e.Graphics;
        if (_wall is not null) g.DrawImageUnscaled(_wall, 0, 0);
        else Theme.PaintWallpaper(g, ClientRectangle);

        // The wordmark lives in the caption strip, on the wallpaper — one identity for the window instead of
        // one inside the nav rail. Its x matches the sidebar rows' text column, so the whole left edge lines up.
        if (!ShowWordmark || CaptionHeight <= Theme.TitleStripH) return;
        int top = CaptionHeight - Theme.TitleStripH;
        if (Logo is not null)
        {
            var im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Logo, new Rectangle(24, top + 5, 20, 20));
            g.InterpolationMode = im;
        }
        TextRenderer.DrawText(g, "Mixtape", _fWordmark, new Rectangle(52, top, 240, Theme.TitleStripH),
            Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnResize(EventArgs e) { _wall?.Dispose(); _wall = null; base.OnResize(e); }

    protected override void Dispose(bool disposing) { if (disposing) { _wall?.Dispose(); _fWordmark.Dispose(); Logo?.Dispose(); } base.Dispose(disposing); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(Cursor.Position);
            bool edge = p.X < ResizeBorder || p.X >= Width - ResizeBorder || p.Y < ResizeBorder || p.Y >= Height - ResizeBorder;
            // The caption strip (minus the window buttons, which are child windows that intercept their
            // own area) and the resize edges fall through to the Form so it can move/snap/resize natively.
            if (edge || p.Y < CaptionHeight) m.Result = (IntPtr)HTTRANSPARENT;
        }
    }
}
