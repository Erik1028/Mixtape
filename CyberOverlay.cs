using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The Cyber look's sweeps and lock-ons (after OrbitOverlay's CyberReveal / ScanH): a cyan scan line with a fading trail
/// running down a page or a dialog as it opens, and yellow corner brackets snapping out to a card's corners on start-up.
/// Drawn in a separate layered window over the target - click-through, never activated, owned by the target's form - so
/// the app under it repaints NOTHING for it: a frame is one UpdateLayeredWindow of a small bitmap (the sweep's strip only
/// moves). With motion off it never appears.
/// </summary>
internal sealed class CyberOverlay : Form
{
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
    private Bitmap? _bmp;
    private IntPtr _hbmp;   // the bitmap as GDI has it - made once per picture (the sweep's strip only moves)

    private CyberOverlay(Form owner)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Owner = owner;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; return cp; }
    }

    protected override bool ShowWithoutActivation => true;

    // never over a harness's near-invisible live test window (it would flash on the screen of whoever is using the PC)
    private static readonly bool Harness = Environment.GetEnvironmentVariable("MIX_LIVE") == "1";

    private static bool Can(Control target) =>
        Theme.Cyber && Anim.MotionEnabled && !Harness && target is { IsDisposed: false, IsHandleCreated: true, Visible: true } && target.FindForm() is { Visible: true } f && f.WindowState != FormWindowState.Minimized;

    /// <summary>A scan line down <paramref name="target"/>: a 2 px cyan line led by a 24 px fading trail.</summary>
    public static void Sweep(Control target, int ms = 360)
    {
        if (!Can(target)) return;
        var area = target.RectangleToScreen(target.ClientRectangle);
        if (area.Width < 40 || area.Height < 40) return;
        var ov = new CyberOverlay(target.FindForm()!) { _bmp = SweepStrip(area.Width) };
        ov.Push(area.X, area.Y - StripH);
        ShowNoActivate(ov);
        Anim.Run(ms, v =>
        {
            if (ov.IsDisposed) return;
            int y = area.Y - StripH + (int)Math.Round((area.Height + StripH) * v);
            ov.Push(area.X, y, clipTo: area);
        }, () => ov.Close(), Easings.OutCubic);
    }

    /// <summary>Start-up (Orbit's lock-on): four yellow L brackets snap out from the centre of <paramref name="target"/> to
    /// its corners, hold, and fade - then a scan line runs down it.</summary>
    public static void Boot(Control target)
    {
        if (!Can(target)) return;
        var area = target.RectangleToScreen(target.ClientRectangle);
        if (area.Width < 80 || area.Height < 80) return;
        // The card-sized picture is a DIB section GDI+ draws into directly: a frame used to copy all of it into a new
        // GDI bitmap first (GetHbitmap, ~1.8 MB) - 3-6 ms of the UI thread each, right while the window starts. The
        // brackets are drawn again only while they move; the hold and the fade only change the window's own alpha.
        var ov = new CyberOverlay(target.FindForm()!);
        ov.NewSurface(area.Width, area.Height);
        float lastK = -1; int lastA = -1;
        void Frame(float k, float alpha)
        {
            int a = (int)Math.Round(255 * Math.Clamp(alpha, 0f, 1f));
            if (k != lastK)
            {
                using (var g = Graphics.FromImage(ov._bmp!)) DrawBrackets(g, area.Size, k, 1f);
                GdiFlush();
                ov.Push(area.X, area.Y, alpha: (byte)a);
            }
            else if (a != lastA) ov.PushAlpha((byte)a);
            lastK = k; lastA = a;
        }
        Frame(0, 1);
        ShowNoActivate(ov);
        Anim.Run(420, v =>
        {
            if (ov.IsDisposed) return;
            float t = (float)v;
            Frame(Math.Min(1f, t / 0.35f), t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f);
        }, () => { ov.Close(); Sweep(target, 420); }, Easings.Linear);
    }

    public const int StripH = 26;

    /// <summary>The harness (--cyberfx-live): frames that reached the screen, and UpdateLayeredWindow calls that failed.</summary>
    internal static int Frames, Failures;

    /// <summary>The harness: run the boot (brackets, then the sweep) over an off-screen window - nothing shows on anyone's
    /// screen - and report what reached the compositor.</summary>
    internal static string LiveSelfTest()
    {
        Theme.SetSkin(false, true);
        bool motion = Anim.MotionEnabled; Anim.MotionEnabled = true;
        using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(700, 420), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Opacity = 0.01 };
        ShowWindow(host.Handle, 4);
        Application.DoEvents();
        Frames = Failures = 0;
        bool can = Theme.Cyber && Anim.MotionEnabled;
        Boot(host);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1300) { Application.DoEvents(); Thread.Sleep(5); }
        int open = Application.OpenForms.OfType<CyberOverlay>().Count();
        Anim.MotionEnabled = motion;
        return $"cyber={can} frames={Frames} failures={Failures} overlays still open={open}" + Environment.NewLine
            + "RESULT: " + (Frames > 10 && Failures == 0 && open == 0 ? "OK" : "FAIL");
    }

    /// <summary>The sweep's picture: a 24 px cyan trail fading up to nothing, led by a 2 px line.</summary>
    public static Bitmap SweepStrip(int width)
    {
        var bmp = new Bitmap(Math.Max(1, width), StripH, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        var c = Theme.CyberCyan;
        using (var trail = new LinearGradientBrush(new Rectangle(0, 0, bmp.Width, StripH - 2), Color.FromArgb(0, c), Color.FromArgb(70, c), LinearGradientMode.Vertical))
            g.FillRectangle(trail, 0, 0, bmp.Width, StripH - 2);
        using var line = new SolidBrush(Color.FromArgb(235, c));
        g.FillRectangle(line, 0, StripH - 2, bmp.Width, 2);
        return bmp;
    }

    /// <summary>The boot's picture at <paramref name="k"/> (0 = at the centre, 1 = at the corners).</summary>
    public static void DrawBrackets(Graphics g, Size area, float k, float alpha)
    {
        g.Clear(Color.Transparent);
        float e = 1 - (1 - k) * (1 - k) * (1 - k);   // from a small box at the centre to the corners, eased
        float hw = 30 + (area.Width / 2f - 6 - 30) * e, hh = 22 + (area.Height / 2f - 6 - 22) * e;
        float cx = area.Width / 2f, cy = area.Height / 2f, arm = 16, w = 3;
        using var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(alpha, 0f, 1f)), Theme.CyberYellow));
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            float x = cx + sx * hw, y = cy + sy * hh;
            g.FillRectangle(b, sx < 0 ? x : x - arm, sy < 0 ? y : y - w, arm, w);
            g.FillRectangle(b, sx < 0 ? x : x - w, sy < 0 ? y : y - arm, w, arm);
        }
    }

    /// <summary>A w×h premultiplied picture that GDI+ draws into and GDI shows as it is - one DIB section, no copies.</summary>
    private void NewSurface(int w, int h)
    {
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        _hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_hbmp == IntPtr.Zero) { _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); return; }   // the copying way
        _dib = true;
        _bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
    }

    private bool _dib;   // _hbmp is the picture's own memory (never re-made from _bmp)

    /// <summary>The same picture with another overall alpha: the window keeps its bitmap, only the blend changes.</summary>
    private void PushAlpha(byte alpha)
    {
        if (IsDisposed || !IsHandleCreated) return;
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        if (UpdateLayeredWindowBlend(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2 /* ULW_ALPHA */)) Frames++; else Failures++;
    }

    /// <summary>Put the bitmap on screen at (x, y) - optionally only the part inside <paramref name="clipTo"/>.</summary>
    private void Push(int x, int y, Rectangle? clipTo = null, byte alpha = 255)
    {
        if (_bmp is null || IsDisposed) return;
        int sx = 0, sy = 0, w = _bmp.Width, h = _bmp.Height;
        if (clipTo is { } c)
        {
            if (y < c.Y) { sy = c.Y - y; h -= sy; y = c.Y; }
            if (y + h > c.Bottom) h = c.Bottom - y;
            if (h <= 0) return;
        }
        if (_hbmp == IntPtr.Zero) _hbmp = _bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), old = SelectObject(mem, _hbmp);
        try
        {
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = sx, y = sy };
            var dst = new POINT { x = x, y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            if (UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */)) Frames++; else Failures++;
        }
        finally
        {
            SelectObject(mem, old); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>The bitmap was drawn again: GDI's copy is stale.</summary>
    private void Repicture() { if (_hbmp != IntPtr.Zero && !_dib) { DeleteObject(_hbmp); _hbmp = IntPtr.Zero; } }

    private static void ShowNoActivate(CyberOverlay ov)
    {
        ShowWindow(ov.Handle, 4 /* SW_SHOWNOACTIVATE */);
    }

    protected override void Dispose(bool disposing)
    {
        Repicture();
        if (disposing) { _bmp?.Dispose(); _bmp = null; }
        if (_dib && _hbmp != IntPtr.Zero) { DeleteObject(_hbmp); _hbmp = IntPtr.Zero; }   // after the Bitmap that wraps its memory
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] private static extern bool UpdateLayeredWindowBlend(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
