using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A borderless dialog's OWN caption strip — so a dialog matches the main window's custom chrome instead of a
/// system title bar. Draws the title on the left over the nav-colour band and the body colour on the right (so it
/// blends seamlessly into a nav+content layout below it), with a close button on the right; the strip is draggable
/// like a real caption (native move + aero-snap). Docks to the top.
/// </summary>
internal sealed class DialogTitleBar : Control
{
    public static int H => Theme.Classic ? Theme.ClassicCaptionH + 4 : 44;   // Classic: the caption + a line of face under it
    private readonly int _navW;   // width of the left (nav) colour band; the rest uses the body colour
    private readonly WindowButton _close = new() { Which = WindowButton.Kind.Close, Width = 46, Height = 32, TabStop = false };
    private readonly Font _font = Theme.UiFont(10f, FontStyle.Bold);

    public DialogTitleBar(string title, int navW)
    {
        Text = title; _navW = navW;
        Dock = DockStyle.Top; Height = H;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Controls.Add(_close);
        _close.Click += (_, _) => FindForm()?.Close();
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) StartDrag(); };
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Theme.Classic)
        {
            _close.Size = new Size(Theme.ClassicBtnW, Theme.ClassicBtnH);
            _close.Location = new Point(Width - 1 - 2 - Theme.ClassicBtnW, 1 + (Theme.ClassicCaptionH - Theme.ClassicBtnH) / 2);
            return;
        }
        _close.Location = new Point(Width - _close.Width - 8, (Height - _close.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Theme.Classic)
        {
            // The navy caption of a 95 window. The FRAME round it belongs to the form (SettingsForm pads itself by
            // two pixels and draws it), so this strip starts inside it and the caption sits where the main
            // window's does: three pixels in from the window's edge.
            using (var face = new SolidBrush(Theme.Face)) g.FillRectangle(face, ClientRectangle);
            var cap = new Rectangle(1, 1, Math.Max(0, Width - 2), Theme.ClassicCaptionH);
            using (var nb = new SolidBrush(Theme.ClassicNavy)) g.FillRectangle(nb, cap);
            TextRenderer.DrawText(g, Text, _font, new Rectangle(cap.X + 4, cap.Y, Math.Max(0, cap.Width - 26), cap.Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        int navW = Math.Max(0, Math.Min(_navW, Width));
        using (var nb = new SolidBrush(Theme.SidebarBg)) g.FillRectangle(nb, 0, 0, navW, Height);     // matches the nav rail below
        using (var bb = new SolidBrush(Theme.Bg)) g.FillRectangle(bb, navW, 0, Width - navW, Height);  // matches the content body below
        TextRenderer.DrawText(g, Text, _font, new Rectangle(16, 0, Math.Max(40, navW - 24), Height), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private void StartDrag() { try { if (FindForm() is { } f) { ReleaseCapture(); SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } } catch { } }   // WM_NCLBUTTONDOWN + HTCAPTION

    protected override void Dispose(bool disposing) { if (disposing) _font.Dispose(); base.Dispose(disposing); }
}
