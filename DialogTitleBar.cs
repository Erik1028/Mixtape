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
    public const int H = 44;
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
        _close.Location = new Point(Width - _close.Width - 8, (Height - _close.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
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
