using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The app's own window chrome for modal dialogs, in place of the native title bar: a borderless, DWM-rounded card
/// with a title strip — the title at the left, a round close button at the right, a hairline under it — that the
/// dialog can be dragged by. Build the content from (0, 0) exactly as before, then call <see cref="AdoptCard"/> once
/// at the end of the constructor: it turns the frame off, grows the client area by the strip's height and moves the
/// content down under it (docked children follow the form's padding, bottom-anchored ones ride the size change).
/// </summary>
internal class CardDialog : GlassDialog
{
    public const int TitleH = 52;
    private const int CloseD = 28, ClosePad = 16;
    private bool _adopted, _closeHover;
    private readonly Font _fTitle = Theme.DisplayFont(14f, FontStyle.Bold);

    /// <summary>False for a dialog that must run to completion (no close button; it closes itself).</summary>
    protected bool ShowClose { get; set; } = true;

    private Rectangle CloseRect => new(ClientSize.Width - ClosePad - CloseD, (TitleH - CloseD) / 2, CloseD, CloseD);

    public void AdoptCard()
    {
        if (_adopted) return;
        _adopted = true;
        // Where the free-floating children sit BEFORE the frame changes. Growing the client area and the padding
        // makes the layout engine move anchored children by itself (by how much depends on whether that child has
        // been laid out yet), so their final places are assigned absolutely at the end instead of nudged.
        var home = new List<(Control c, Point at)>();
        foreach (Control c in Controls) if (c.Dock == DockStyle.None && (c.Anchor & AnchorStyles.Bottom) == 0) home.Add((c, c.Location));
        var cs = ClientSize;
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = MinimizeBox = false;
        Padding = new Padding(Padding.Left, Padding.Top + TitleH, Padding.Right, Padding.Bottom);   // docked children start under the strip
        ClientSize = new Size(cs.Width, cs.Height + TitleH);                                        // bottom-anchored children ride down with this
        ResumeLayout(true);
        foreach (var (c, at) in home) c.Location = new Point(at.X, at.Y + TitleH);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseDown += OnChromeDown;
        MouseMove += (_, e) => SetCloseHover(ShowClose && CloseRect.Contains(e.Location));
        MouseLeave += (_, _) => SetCloseHover(false);
    }

    private void OnChromeDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Y >= TitleH) return;
        if (ShowClose && CloseRect.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); return; }
        try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { }   // WM_NCLBUTTONDOWN / HTCAPTION: drag by the strip
    }

    private void SetCloseHover(bool on) { if (_closeHover == on) return; _closeHover = on; Invalidate(CloseRect); }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);   // dark frame + DWMWCP_ROUND + the caption colour (GlassDialog)
        try { int bc = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { }   // a subtle border, like MessageDialog
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_adopted) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int right = ShowClose ? ClosePad + CloseD + 12 : 22;
        TextRenderer.DrawText(g, Text, _fTitle, new Rectangle(22, 0, Math.Max(10, ClientSize.Width - 22 - right), TitleH), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (ShowClose)
        {
            var r = CloseRect;
            using (var b = new SolidBrush(Color.FromArgb(_closeHover ? 62 : 28, 255, 255, 255))) g.FillEllipse(b, r);
            using var pen = new Pen(Theme.TextCol, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = 4.5f;
            g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
            g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
        }
        g.SmoothingMode = SmoothingMode.None;
        using var hair = new Pen(Theme.Blend(BackColor, Color.White, 0.08));
        g.DrawLine(hair, 0, TitleH - 1, ClientSize.Width, TitleH - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fTitle.Dispose();
        base.Dispose(disposing);
    }
}
