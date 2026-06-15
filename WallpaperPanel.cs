namespace iPodCommander;

/// <summary>
/// The shell root: paints the themed gradient "wallpaper" + soft shadows behind the floating cards,
/// and reserves a top caption strip (the app's own title bar). Near the window edges and across the
/// caption strip it reports HTTRANSPARENT so the parent Form's WndProc can do native resize / drag /
/// snap (we removed the OS title bar). Window control buttons are children and intercept their own area.
/// </summary>
internal sealed class WallpaperPanel : Panel
{
    public int CaptionHeight = 36;
    public int ResizeBorder = 6;

    public WallpaperPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Theme.PaintWallpaper(e.Graphics, ClientRectangle);
        foreach (Control c in Controls)
            if (c.Visible && c is not WindowButton) Theme.PaintCardShadow(e.Graphics, c.Bounds, 16);
    }

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
