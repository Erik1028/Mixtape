using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A borderless, DWM-rounded dropdown/flyout window (Apple-style popover): it appears anchored to a button
/// (dropping up from a bottom-bar control), floats above the app with smoothly rounded corners + the native
/// rounded shadow, and dismisses itself the moment focus leaves it (click-away) or Esc is pressed. Used for
/// the Equalizer and Pro-features panels so they read as dropdowns rather than separate windows. Disposes
/// itself on close.
/// </summary>
internal abstract class FlyoutForm : Form
{
    private const float Radius = 9f;   // matches the DWM "round" corner radius

    protected FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        TopMost = true;          // float above the (possibly top-most) mini player
        DoubleBuffered = true;
        BackColor = Theme.Bg;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }                  // dark mode
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }                // DWMWCP_ROUND
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, sizeof(int)); } catch { } // no DWM border line
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!IsDisposed) Close(); }   // click-away dismiss

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // A faint rounded edge that aligns with the DWM-rounded corners (the OS clips the window to this radius).
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.12));
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); Dispose(); }

    /// <summary>Show anchored to a button's screen rectangle — drops UP from a bottom-bar control, right-aligned,
    /// clamped to the working area (flips below if there's no room above).</summary>
    public void ShowAnchored(Rectangle anchorScreen)
    {
        var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
        int x = Math.Clamp(anchorScreen.Right - Width, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - Width - 4));
        int y = anchorScreen.Top - Height - 6;
        if (y < wa.Top + 4) y = Math.Min(anchorScreen.Bottom + 6, wa.Bottom - Height - 4);
        Location = new Point(x, y);
        Show();
        Activate();
    }
}
