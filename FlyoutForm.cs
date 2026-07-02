using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace iPodCommander;

/// <summary>
/// A borderless, DWM-rounded dropdown/flyout window (Apple-style popover): it appears anchored to a button
/// (dropping up from a bottom-bar control), floats above the app with smoothly rounded corners + the native
/// rounded shadow, and dismisses itself the moment focus leaves it (click-away) or Esc is pressed. Used for
/// the Equalizer and Pro-features panels so they read as dropdowns rather than separate windows. Disposes
/// itself on close.
/// </summary>
internal abstract class FlyoutForm : Form, IGlassHost
{
    private const float Radius = 9f;   // matches the DWM "round" corner radius
    private const int GlassMargin = 40;   // padding around the popup for the CA offsets + settle overshoot (max sample ≈ 18px·1.8·1.10 ≈ 36px < 39) — was 60 when the rim still sampled outward; 40 shrinks every per-frame blur/warp buffer ~25%
    private Bitmap? _frost;            // the refracted liquid-glass backdrop painted behind this flyout (UI thread only)
    // LIVE glass, BUFFERED: a UI-thread timer re-captures the FULL region behind the flyout each tick (owner.DrawToBitmap — so
    // ALL moving content shows, not just one control) and hands its pixels to a background worker that does the heavy blur+refract
    // OFF the UI thread, posting the finished frame back via an int[] hand-off. The capture must stay on the UI thread, but the
    // warp doesn't — so it's much lighter than the original all-on-UI-thread version. The worker touches ONLY thread-local bitmaps.
    private System.Windows.Forms.Timer? _glassTimer;   // UI: capture cadence
    private Thread? _glassWorker;                       // bg: blur + refract
    private readonly object _glassLock = new();
    private volatile bool _glassRun;
    private int[]? _basePx; private int _baseW, _baseH; private bool _reqPending;       // UI → worker (latest composited region behind the flyout)
    private int[]? _resultPx; private int _resW, _resH;                                 // worker → UI (finished frame pixels)
    // The backdrop is captured ONCE (`_baseStatic`); each frame composites onto a reused copy (`_capSlice`) only what MOVES:
    // the now-playing bar (re-rendered — one small control, cheap) and the song list's scroll slice (re-cut from MainForm's
    // cached snap — a copy, not a render). That kills the per-tick full-window DrawToBitmap (the real fps cost), not just the GC.
    private Bitmap? _baseStatic, _capSlice, _barBuf;
    public Bitmap? GlassFrost => _frost;
    public Color GlassTint => Glass.SurfaceTint;   // flyouts stay more see-through than the modal dialogs
    protected bool GlassEnabled;      // opt-in: only flyouts that set this capture + paint the frosted-glass backdrop

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
        // Glass: set BackColor to the glass base so the rounded-corner antialiasing blends into the glass rather than
        // the darker Theme.Bg (that fringe read as a thin edge at the corners).
        if (GlassEnabled) BackColor = Theme.SidebarBg;
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!IsDisposed) Close(); }   // click-away dismiss

    // Frosted-glass backdrop: the blurred app content behind the flyout, painted as the window background so the
    // glass-aware child controls slice their own piece of it. Captured in ShowAnchored before the flyout is shown.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_frost is null) { base.OnPaintBackground(e); return; }
        var g = e.Graphics;
        var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 1:1, exact copy (no edge fringe)
        g.DrawImage(_frost, new Rectangle(0, 0, Width, Height));
        g.InterpolationMode = im;
        using var tint = new SolidBrush(Glass.SurfaceTint);
        g.FillRectangle(tint, 0, 0, Width, Height);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        base.OnKeyDown(e);
    }

    // Cached silhouette geometry for the rim glint (static per size — only the per-vertex alphas animate).
    private GraphicsPath? _glintPath; private PointF[]? _glintPts; private float[]? _glintPhi; private int _glintW, _glintH;

    /// <summary>Flatten the rounded-rect outline once per size and precompute each vertex's OUTWARD normal angle
    /// (screen coords, y down; oriented via the rect centre — no winding assumption).</summary>
    private void EnsureGlintGeometry()
    {
        if (_glintPath is not null && _glintW == Width && _glintH == Height) return;
        _glintPath?.Dispose();
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        var flat = (GraphicsPath)path.Clone();
        flat.Flatten(null, 0.25f);
        var pts = flat.PathPoints;
        var phi = new float[pts.Length];
        float cx = Width / 2f, cy = Height / 2f;
        for (int i = 0; i < pts.Length; i++)
        {
            var prev = pts[(i - 1 + pts.Length) % pts.Length];
            var next = pts[(i + 1) % pts.Length];
            float nx = next.Y - prev.Y, ny = -(next.X - prev.X);                       // perpendicular of the tangent across the vertex
            if (nx * (pts[i].X - cx) + ny * (pts[i].Y - cy) < 0) { nx = -nx; ny = -ny; }   // orient outward
            phi[i] = (float)Math.Atan2(ny, nx);
        }
        _glintPath = flat; _glintPts = pts; _glintPhi = phi; _glintW = Width; _glintH = Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_frost is not null)
        {
            // Liquid-glass edge GLINT, geometry-responsive like Apple's: intensity follows the angle between the
            // silhouette's outward normal and a virtual light at -120° (upper-left, 30° above horizontal — screen
            // coords), so the top-left corner arc catches the key light and the OPPOSITE bottom-right arc gets the
            // weaker TRANSMITTED glint (light passes through the glass and lights the far edge — the two-glint look).
            // Still a 1.1px crisp hairline: it refracts/catches light, it is not a glow band. During the settle
            // wobble the light SWINGS with the jelly and the hairline flares slightly ("lights move in space").
            EnsureGlintGeometry();
            if (_glintPts is { Length: >= 3 })
            {
                const float L0 = -120f * (float)(Math.PI / 180.0);
                float swing = Math.Clamp(43.75f * (_magScale - 1f), -16f, 35f) * (float)(Math.PI / 180.0);
                float L = L0 - swing;
                float boost = Math.Min(1.25f, 1f + 0.35f * Math.Max(0f, _magScale - 1f));
                var cols = new Color[_glintPts.Length];
                for (int i = 0; i < cols.Length; i++)
                {
                    float c1 = (float)Math.Cos(_glintPhi![i] - L);
                    float p1 = Math.Max(0f, c1), p2 = Math.Max(0f, -c1);
                    float a = 12f + (115f * p1 * p1 * p1 + 65f * p2 * p2 * p2) * boost;   // key lobe + weaker transmitted lobe
                    cols[i] = Color.FromArgb(Math.Clamp((int)a, 0, 255), 255, 255, 255);
                }
                using var glint = new PathGradientBrush(_glintPts) { CenterColor = Color.FromArgb(40, 255, 255, 255) };
                glint.SurroundColors = cols;   // one seamless AA stroke — per-segment lines would bead at shared endpoints
                using var gpen = new Pen(glint, 1.1f);
                e.Graphics.DrawPath(gpen, _glintPath!);
            }
            return;
        }
        // A faint rounded edge that aligns with the DWM-rounded corners (the OS clips the window to this radius).
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        using var pen = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.12));
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_scrollSrc is not null) { _scrollSrc.ListScrolled -= OnBackdropScrolled; _scrollSrc = null; }
        StopSettle();
        _glintPath?.Dispose(); _glintPath = null;
        _glassTimer?.Stop(); _glassTimer?.Dispose(); _glassTimer = null;   // stop the UI capture first → no more RequestGlassFrame touching the capture buffers
        lock (_glassLock) { _glassRun = false; Monitor.Pulse(_glassLock); }   // wake the worker so it breaks its loop + disposes its bitmaps
        _frost?.Dispose(); _frost = null;
        _baseStatic?.Dispose(); _baseStatic = null;
        _capSlice?.Dispose(); _capSlice = null;
        _barBuf?.Dispose(); _barBuf = null;
        base.OnFormClosed(e); Dispose();
    }

    /// <summary>Capture the region behind the flyout ONCE and paint a crisp first frame (2× supersample, no flash). Returns
    /// true so the caller starts the live pipeline AFTER Show() (handle + message loop guaranteed up).</summary>
    private bool OpenGlass()
    {
        if (!GlassEnabled || IsDisposed) return false;
        try
        {
            var raw = Glass.CapturePaddedRaw(this, Owner, GlassMargin);
            if (raw is null) return false;
            int k = 3, pw = raw.Width, ph = raw.Height;
            using (var tiny = new Bitmap(Math.Max(8, pw / k), Math.Max(8, ph / k)))
            using (var blur = new Bitmap(pw, ph))
            {
                float luma = Glass.BlurInto(raw, blur, tiny, GlassMargin / k);
                Glass.LiveTintAlpha = Glass.TintTargetFor(luma);   // HARD-set on open — reopening over different content must not visibly ramp
                var bent = Glass.Refract(blur, GlassMargin, 2, SettleStart);   // crisp first frame, AT the settle's opening strength (no jump when the wobble takes over)
                var old = _frost; _frost = bent; old?.Dispose();
            }
            if (!IsDisposed) Invalidate(true);
            _baseStatic?.Dispose(); _baseStatic = raw;   // KEEP the sharp static backdrop (list etc.) for the cheap per-tick composite
            // Pin the padded backdrop's screen origin AT CAPTURE TIME: the open animation slides the window 8px,
            // and mapping live frames at the CURRENT position against a base captured at the FINAL one ghosted
            // the bar/list during the slide. All composites use this fixed origin instead.
            _basePadOrigin = PointToScreen(Point.Empty);
            _basePadOrigin.Offset(-GlassMargin, -GlassMargin);
            return true;
        }
        catch { _frost = null; return false; }
    }

    /// <summary>Start the live pipeline: a UI-thread timer re-renders ONLY the now-playing bar each tick + composites it onto the
    /// cached static backdrop, and the worker warps the result OFF the UI thread. Called AFTER Show() so the handle/loop are up.</summary>
    private void StartLiveGlass()
    {
        if (IsDisposed || _baseStatic is null || Owner is not MainForm mf) return;   // need the bar (MainForm) + the cached backdrop
        _glassRun = true;
        _glassWorker = new Thread(GlassWorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FlyoutGlass" };
        _glassWorker.Start();
        _glassTimer = new System.Windows.Forms.Timer { Interval = 80 };   // ~12fps capture; the heavy blur+refract is OFF the UI thread, so the UI only pays the DrawToBitmap
        _glassTimer.Tick += (_, _) => RequestGlassFrame();
        _glassTimer.Start();
        // Scroll must not wait out the timer tick — that read as lag. Every list scroll pokes an IMMEDIATE frame
        // that reuses the bar's last pixels (no DrawToBitmap: just the composite memcpy), so the glass tracks the
        // scroll in real time like the bar's own frost; the worker coalesces if frames arrive faster than it warps.
        _scrollSrc = mf;
        mf.ListScrolled += OnBackdropScrolled;
        StartSettle(SettleStart, SettleMs);   // the opening "liquid" wobble (the crisp first frame was rendered at SettleStart strength)
    }

    private MainForm? _scrollSrc;   // the MainForm whose ListScrolled we subscribed (detached in OnFormClosed)
    private void OnBackdropScrolled() => RequestGlassFrame(refreshBar: false);

    // ---- "liquid settle": on open (and on press release) the refraction lands with a damped wobble (overshoot →
    // ripple → rest), like the glass flexing. The worker multiplies _magScale into the displacement each frame. ----
    private const float SettleStart = 1.8f;      // opening overshoot — with the ~16-18px rest displacement this stays under the margin cap, so the wobble renders un-clipped
    private const int SettleMs = 380;
    private const float PressFlex = 1.18f;       // held while the mouse is down; springs back on release (Apple: the glass flexes the instant you touch it)
    private volatile float _magScale = SettleStart;
    private volatile bool _settling;             // worker renders 2×2-supersampled frames while the wobble moves (rim aliasing is worst then)
    private bool _pressed;
    private System.Windows.Forms.Timer? _settleTimer;

    private void StopSettle() { _settleTimer?.Stop(); _settleTimer?.Dispose(); _settleTimer = null; _settling = false; }

    private void StartSettle(float from, int ms)
    {
        StopSettle();   // a press during the open settle must not leave an orphaned timer fighting over _magScale
        _settling = true;
        int t0 = Environment.TickCount;
        _settleTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _settleTimer.Tick += (_, _) =>
        {
            float t = (Environment.TickCount - t0) / (float)ms;
            if (t >= 1f) { _magScale = 1f; StopSettle(); }
            else _magScale = 1f + (from - 1f) * (1f - t) * (1f - t) * (float)Math.Cos(t * 7.4f);   // damped cosine → jelly
            RequestGlassFrame(refreshBar: false);   // poke a frame at the new strength (worker coalesces)
        };
        _settleTimer.Start();
    }

    /// <summary>Apple's glass "flexes" the instant you touch it and springs back on release. Press = HOLD the lens
    /// at a constant extra bend (no animation while held — costs nothing; scroll/timer frames pick it up); release =
    /// the proven damped settle. Hooked recursively on every child so a press anywhere in the flyout flexes the panel.</summary>
    private void HookPress(Control c)
    {
        c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && _glassRun) { StopSettle(); _pressed = true; _magScale = PressFlex; RequestGlassFrame(refreshBar: false); } };
        c.MouseUp += (_, _) => { if (_pressed) { _pressed = false; StartSettle(PressFlex, 320); } };   // guard: no phantom pulse from a MouseUp whose press began outside
        foreach (Control child in c.Controls) HookPress(child);
    }

    /// <summary>UI thread, per tick: re-render ONLY the now-playing bar (one small control — cheap, vs the whole window), overlay
    /// the song list's live scroll slice (a re-slice of MainForm's cached snap — a copy, not a render), composite both onto a
    /// reused copy of the cached static backdrop, copy the result to the worker, wake it. Everything else stays static — a
    /// full-window DrawToBitmap per tick is what janked scrolling originally.</summary>
    private Rectangle _barRectScreen;   // the bar's screen rect from its last capture (the bar doesn't move while a flyout is open)
    private Point _basePadOrigin;       // the padded backdrop's screen origin AT CAPTURE TIME (fixed — see OpenGlass)

    private void RequestGlassFrame(bool refreshBar = true)
    {
        if (IsDisposed || !_glassRun || _baseStatic is null || Owner is not MainForm mf) return;
        try
        {
            if (refreshBar || _barBuf is null)   // scroll pokes skip this — they reuse the bar's last pixels
            {
                var rect = mf.CaptureNowPlayingInto(ref _barBuf);   // cheap: one control's DrawToBitmap
                if (rect is null || _barBuf is null) return;
                _barRectScreen = rect.Value;
            }
            int pw = _baseStatic.Width, ph = _baseStatic.Height;
            // the bar's top-left INSIDE the padded backdrop — screen-space delta against the FIXED capture origin
            var barInPad = new Rectangle(_barRectScreen.X - _basePadOrigin.X, _barRectScreen.Y - _basePadOrigin.Y, _barBuf.Width, _barBuf.Height);

            if (_capSlice is null || _capSlice.Width != pw || _capSlice.Height != ph) { _capSlice?.Dispose(); _capSlice = new Bitmap(pw, ph, PixelFormat.Format32bppArgb); }
            using (var g = Graphics.FromImage(_capSlice))
            {
                g.DrawImageUnscaled(_baseStatic, 0, 0);
                // The song list's LIVE pixels: MainForm re-slices its cached full-height list snap at the CURRENT
                // scroll offset (a plain copy — no DrawToBitmap), so scrolling behind the flyout moves in the glass.
                // Views without a snap (browse/photos/device) just keep the static backdrop.
                mf.DrawLiveListInto(g, _basePadOrigin);
                g.DrawImage(_barBuf, barInPad);   // the bar's CURRENT pixels on top (it overlaps the list's bottom)
            }
            var bd = _capSlice.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                lock (_glassLock)
                {
                    if (_basePx is null || _basePx.Length != pw * ph) _basePx = new int[pw * ph];
                    Marshal.Copy(bd.Scan0, _basePx, 0, pw * ph);   // stride == pw*4 for 32bpp → a flat copy is exact
                    _baseW = pw; _baseH = ph; _reqPending = true;
                    Monitor.Pulse(_glassLock);
                }
            }
            finally { _capSlice.UnlockBits(bd); }
        }
        catch { }
    }

    /// <summary>Background worker: waits for the latest captured region, blurs + refracts it into THREAD-LOCAL bitmaps (never
    /// shared with the UI thread → no GDI+ cross-thread hazard), and posts the finished pixels back. Only the latest request
    /// matters (coalesced), so a slow frame never queues up.</summary>
    private void GlassWorkerLoop()
    {
        Bitmap? src = null, blur = null, tiny = null, result = null;
        try
        {
            while (true)
            {
                int pw, ph;
                lock (_glassLock)
                {
                    while (_glassRun && !_reqPending) Monitor.Wait(_glassLock);
                    if (!_glassRun) break;
                    pw = _baseW; ph = _baseH; _reqPending = false;
                    if (src is null || src.Width != pw || src.Height != ph)
                    {
                        src?.Dispose(); src = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
                        blur?.Dispose(); blur = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
                        tiny?.Dispose(); tiny = new Bitmap(Math.Max(8, pw / 3), Math.Max(8, ph / 3), PixelFormat.Format32bppArgb);
                        int w = pw - 2 * GlassMargin, h = ph - 2 * GlassMargin;
                        result?.Dispose(); result = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
                    }
                    var sd = src.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(_basePx!, 0, sd.Scan0, pw * ph);
                    src.UnlockBits(sd);
                }
                float luma = Glass.BlurInto(src, blur!, tiny!, GlassMargin / 3);   // mean backdrop luma, measured under the panel (margin excluded)
                // Adaptive dimming layer (HIG): dark backdrop → the tuned 136 stays; bright/art-heavy → up to 176.
                // Lerped so scrolling art doesn't flicker the tint; children repaint via SwapGlassFrame's Invalidate.
                Glass.LiveTintAlpha = (int)Math.Round(Glass.LiveTintAlpha * 0.8f + Glass.TintTargetFor(luma) * 0.2f);
                // 2×2 supersample while the wobble MOVES (open/press-release — rim aliasing is worst then), 1× at rest.
                Glass.RefractInto(blur!, result!, GlassMargin, _settling ? 2 : 1, _magScale);
                int rw = result!.Width, rh = result.Height;
                var rd = result.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                lock (_glassLock)
                {
                    if (_resultPx is null || _resultPx.Length != rw * rh) _resultPx = new int[rw * rh];
                    Marshal.Copy(rd.Scan0, _resultPx, 0, rw * rh);
                    _resW = rw; _resH = rh;
                }
                result.UnlockBits(rd);
                try { if (_glassRun && IsHandleCreated && !IsDisposed) BeginInvoke((Action)SwapGlassFrame); } catch { }
            }
        }
        catch { }
        finally { src?.Dispose(); blur?.Dispose(); tiny?.Dispose(); result?.Dispose(); }
    }

    /// <summary>UI thread: copy the worker's finished frame into the displayed <see cref="_frost"/> buffer + repaint.</summary>
    private void SwapGlassFrame()
    {
        if (IsDisposed || _frost is null) return;
        try
        {
            lock (_glassLock)
            {
                if (_resultPx is null || _frost.Width != _resW || _frost.Height != _resH) return;
                var fd = _frost.LockBits(new Rectangle(0, 0, _resW, _resH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(_resultPx, 0, fd.Scan0, _resW * _resH);
                _frost.UnlockBits(fd);
            }
            Invalidate(true);   // repaint the whole flyout + glass-aware children so the live backdrop change is never hidden
        }
        catch { }
    }

    /// <summary>Show anchored to a button's screen rectangle — drops UP from a bottom-bar control, right-aligned,
    /// clamped to the working area (flips below if there's no room above).</summary>
    public void ShowAnchored(Rectangle anchorScreen)
    {
        var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
        int x = Math.Clamp(anchorScreen.Right - Width, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - Width - 4));
        int y = anchorScreen.Top - Height - 6;
        if (y < wa.Top + 4) y = Math.Min(anchorScreen.Bottom + 6, wa.Bottom - Height - 4);
        Location = new Point(x, y);
        bool live = GlassEnabled && OpenGlass();   // crisp first frame BEFORE Show (no flash)
        bool motion = Anim.MotionEnabled;
        // Glass MATERIALIZES instead of fading (Apple: "Liquid Glass objects materialize by gradually modulating the
        // light bending and lensing" — the settle wobble carries the appear energy); the 0.85 start only hides the
        // Show() pop, snapping to 1 in 90ms. Plain (non-glass) popups keep the classic full fade. Pre-Show so the
        // first paint is never a full-opacity flash.
        if (motion) { Opacity = live ? 0.85 : 0; Top = y + 8; }
        Show();
        Activate();
        if (live) { StartLiveGlass(); HookPress(this); }   // live pipeline AFTER Show (handle + loop are up); press-flex hooks once all children exist

        if (!motion) return;
        int home = y;
        Anim.Run(170, v => { if (IsDisposed) return; Top = home + (int)Math.Round(8 * (1 - v)); },
            () => { if (!IsDisposed) Top = home; }, Easings.OutCubic);
        if (live) Anim.Run(90, v => { if (IsDisposed) return; Opacity = 0.85 + 0.15 * v; }, () => { if (!IsDisposed) Opacity = 1; }, Easings.OutCubic);
        else Anim.Run(170, v => { if (IsDisposed) return; Opacity = v; }, () => { if (!IsDisposed) Opacity = 1; }, Easings.OutCubic);
    }
}
