namespace iPodCommander;

/// <summary>
/// The Windows 95 look's status bar: sunken panels along the bottom of the window carrying what the header and
/// the rail already know - what is listed and selected, the iPod and its free space, whether anything plays -
/// and the size grip in the corner. It holds no state of its own: it asks for its three texts every time it
/// paints, and the host invalidates it when one of them changes.
/// </summary>
internal sealed class ClassicStatusBar : Control
{
    public const int H = 20;
    private const int Grip = 16, PanelGap = 2;
    private readonly Func<(string Main, string Device, string Play)> _text;
    private readonly Font _font = Theme.UiFont(Theme.SzBody);

    public ClassicStatusBar(Func<(string Main, string Device, string Play)> text)
    {
        _text = text;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = H;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Face);
        var (main, device, play) = _text();
        int right = Width - Grip;
        int playW = Math.Max(70, TextRenderer.MeasureText(play, _font).Width + 12);
        int devW = Math.Min(240, Math.Max(90, TextRenderer.MeasureText(device, _font).Width + 12));
        var pPlay = new Rectangle(right - playW, 2, playW, Height - 2);
        var pDev = new Rectangle(pPlay.X - PanelGap - devW, 2, devW, Height - 2);
        var pMain = new Rectangle(0, 2, Math.Max(0, pDev.X - PanelGap), Height - 2);
        Panel(g, pMain, main);
        Panel(g, pDev, device);
        Panel(g, pPlay, play);
        PaintGrip(g);
    }

    /// <summary>One status panel: a thin sunken edge and its text, cut with an ellipsis when it does not fit.</summary>
    private void Panel(Graphics g, Rectangle r, string text)
    {
        if (r.Width < 10) return;
        Theme.Bevel(g, r, raised: false, thin: true);
        TextRenderer.DrawText(g, text, _font, new Rectangle(r.X + 4, r.Y, r.Width - 8, r.Height), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>The size grip: the corner's diagonal ridges, a highlight line and two shadow lines per ridge.</summary>
    private void PaintGrip(Graphics g)
    {
        using var hi = new Pen(Theme.FaceHi);
        using var sh = new Pen(Theme.FaceShadow);
        int w = Width, h = Height;
        for (int k = 1; k <= 12; k++)
        {
            var pen = (k % 4) switch { 1 => hi, 2 => sh, 3 => sh, _ => null };
            if (pen is null) continue;
            g.DrawLine(pen, w - 1 - k, h - 1, w - 1, h - 1 - k);
        }
    }

    // The grip is the window's own corner: over it the hit test falls through to the form, which answers
    // HTBOTTOMRIGHT there - so dragging the grip resizes the window natively, snapping and all.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST && PointToClient(Cursor.Position).X >= Width - Grip) m.Result = (IntPtr)HTTRANSPARENT;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
