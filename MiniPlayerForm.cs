using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// iTunes / Windows-Media-Player-style MiniPlayer: a small floating window that mirrors and drives the
/// main window's single audio engine (via <see cref="NowPlayingBar"/>'s facade). Layout: a title bar
/// (leave-mini arrow · app icon · name · minimize / maximize / close), a large blurred-album-art hero
/// with the track text overlaid lower-left, and a floating dark "glass" control panel holding one row
/// of controls (volume · shuffle · prev · play · next · repeat · more) above a seek row. Icons are
/// Segoe Fluent / MDL2 glyphs to match the Windows look. The window is draggable; the leave-mini arrow,
/// maximize, close and Esc all return to the full window (the mini never quits the app on its own).
/// </summary>
internal sealed class MiniPlayerForm : Form
{
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? PlayPauseRequested;
    public event Action<double>? SeekRequested;     // 0..1
    public event Action<double>? VolumeRequested;    // 0..1
    public event Action? MuteRequested;
    public event Action? ShuffleRequested;
    public event Action? RepeatRequested;
    public event Action<Rectangle>? EqualizerRequested;     // arg = anchor screen rect for the flyout
    public event Action<Rectangle>? ProFeaturesRequested;   // open the Pro-features hub
    public event Action? ExpandRequested;            // return to the full window

    private Track? _track;
    private Bitmap? _cover;
    private Bitmap? _backdrop;                 // cached blurred ambient background (rebuilt per track)
    private Bitmap? _iconBmp;                  // cached app icon for the title bar
    private bool _playing, _shuffle, _muted;
    private bool _compact;                     // small ↔ normal control UI (toggled by the maximize button)
    private NowPlayingBar.RepeatMode _repeat;
    private double _dur, _vol = 1;
    private double _posBase;                   // engine position at the last push
    private readonly Stopwatch _sw = new();    // interpolates between ~5 Hz pushes for a smooth seek bar
    private Tween? _anim;                       // ~30 fps repaint of the moving seek strip while playing
    private double _scrubFrac = -1;            // while dragging the seek bar
    public Func<float[], bool>? SpectrumProvider;   // host fills 0..1 spectrum bands; returns false when silent/paused
    private readonly float[] _spec = new float[28], _specTmp = new float[28];   // smoothed spectrum + scratch

    private enum Hit { None, Restore, Min, Max, Close, Volume, Shuffle, Prev, Play, Next, Repeat, More }
    private Hit _hover = Hit.None;

    private const int W = 360, H = 500, TitleH = 42;
    private static readonly Size NormalSize = new(W, H), CompactSize = new(300, 398);

    // Segoe Fluent (Win11) / MDL2 (Win10) icon glyphs — the family the reference uses. Built from char
    // codes because literal PUA glyphs get stripped when written into source.
    private static readonly string? IconFont = ResolveIconFont();
    private static string? ResolveIconFont()
    {
        foreach (var n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            try { using var ff = new FontFamily(n); return n; } catch { }
        return null;
    }
    private static string Ch(int cp) => ((char)cp).ToString();
    private static readonly string GRestore = Ch(0xE944), GMin = Ch(0xE921), GMax = Ch(0xE922), GRestoreDown = Ch(0xE923), GClose = Ch(0xE8BB);
    private static readonly string GVol = Ch(0xE767), GMute = Ch(0xE74F), GShuffle = Ch(0xE8B1), GPrev = Ch(0xE892), GNext = Ch(0xE893);
    private static readonly string GPlay = Ch(0xE768), GPause = Ch(0xE769), GRepeatAll = Ch(0xE8EE), GRepeatOne = Ch(0xE8ED), GMore = Ch(0xE712);

    public MiniPlayerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(W, H);
        BackColor = Theme.SidebarBg;
        Text = "Mixtape — Mini Player";
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        try { if (Environment.ProcessPath is string p) { Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); _iconBmp = Icon?.ToBitmap(); } } catch { }

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { if (_hover != Hit.None) { _hover = Hit.None; Invalidate(); } };
        DoubleClick += (_, _) => { var p = PointToClient(Cursor.Position); if (p.Y > TitleH && !Layout().Panel.Contains(p)) ExpandRequested?.Invoke(); };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) ExpandRequested?.Invoke();
            else if (e.KeyCode == Keys.Space) { PlayPauseRequested?.Invoke(); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) NextRequested?.Invoke();
            else if (e.KeyCode == Keys.Left) PrevRequested?.Invoke();
        };
    }

    // ---- host → mini state ----

    /// <summary>Set the now-playing track + cover (TAKES OWNERSHIP of <paramref name="cover"/>).</summary>
    public void SetTrack(Track? track, Bitmap? cover)
    {
        _track = track;
        var old = _cover; _cover = cover; old?.Dispose();
        BuildBackdrop();
        Invalidate();
    }

    /// <summary>Push live playback state (cheap; called on every engine tick + state change).</summary>
    public void SetProgress(bool playing, double posSec, double durSec, double volume, bool muted, bool shuffle, NowPlayingBar.RepeatMode repeat)
    {
        _playing = playing; _dur = durSec; _vol = volume; _muted = muted; _shuffle = shuffle; _repeat = repeat;
        _posBase = posSec; _sw.Restart();
        if (playing && Visible) StartAnim(); else StopAnim();
        Invalidate();
    }

    private double DisplayPos()
    {
        double p = _posBase + (_playing ? _sw.Elapsed.TotalSeconds : 0);
        return _dur > 0 ? Math.Min(_dur, Math.Max(0, p)) : Math.Max(0, p);
    }

    // A soft blurred wash of the cover (Apple-Music / WMP "now playing" feel). Cheap blur: downsample the
    // cover hard, then stretch it over the whole window. Legibility scrims are drawn live in OnPaint.
    private void BuildBackdrop()
    {
        _backdrop?.Dispose(); _backdrop = null;
        if (_cover is null) return;
        int w = ClientSize.Width, h = ClientSize.Height;
        var bd = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bd))
        using (var small = new Bitmap(20, 20))
        {
            using (var gs = Graphics.FromImage(small)) { gs.InterpolationMode = InterpolationMode.HighQualityBilinear; gs.PixelOffsetMode = PixelOffsetMode.HighQuality; gs.DrawImage(_cover, new Rectangle(0, 0, 20, 20)); }
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, new Rectangle(-8, -8, w + 16, h + 16)); // overscan so card edges aren't a single sampled row
        }
        _backdrop = bd;
    }

    private void StartAnim()
    {
        if (_anim is { IsRunning: true } || !Anim.MotionEnabled) return;
        int tick = 0;
        _anim = Anim.Run(1_000_000_000, _ =>
        {
            if (IsDisposed) return;
            UpdateSpectrum();
            if ((++tick & 1) == 0) { Invalidate(SeekStrip()); Invalidate(Layout().Spectrum); }
        }, null, Easings.Linear);
    }
    private void StopAnim() { _anim?.Cancel(); _anim = null; Array.Clear(_spec); if (!IsDisposed && IsHandleCreated) Invalidate(Layout().Spectrum); }

    // Ease the spectrum toward the host's live bands (fast attack, slow decay); falls to zero when paused/silent.
    private void UpdateSpectrum()
    {
        bool live = SpectrumProvider?.Invoke(_specTmp) ?? false;
        for (int i = 0; i < _spec.Length; i++)
        {
            float target = live ? _specTmp[i] : 0f;
            _spec[i] += (target - _spec[i]) * (target > _spec[i] ? 0.55f : 0.16f);
        }
    }

    /// <summary>Render-only: inject a static spectrum so a screenshot shows the bars (no live audio in renders).</summary>
    public void DebugSpectrum(float[] b) { for (int i = 0; i < _spec.Length; i++) _spec[i] = i < b.Length ? b[i] : 0f; Invalidate(); }
    private Rectangle SeekStrip() { var l = Layout(); return new Rectangle(l.Panel.X, l.Seek.Y - 14, l.Panel.Width, 30); }

    // ---- chrome: dark, rounded (DWM), with the standard drop shadow — like the main window ----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, sizeof(int)); } catch { }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _playing) StartAnim(); else if (!Visible) StopAnim();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; ExpandRequested?.Invoke(); return; }
        base.OnFormClosing(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        double v = Math.Clamp((_muted ? 0 : _vol) + (e.Delta > 0 ? 0.06 : -0.06), 0, 1);
        _vol = v; _muted = v <= 0.001;
        VolumeRequested?.Invoke(v);
        Invalidate();
    }

    /// <summary>Switch between the small (compact) and normal control UI, keeping the window centred.</summary>
    public void SetCompact(bool compact)
    {
        _compact = compact;
        var center = new Point(Left + Width / 2, Top + Height / 2);
        ClientSize = compact ? CompactSize : NormalSize;
        var wa = Screen.FromControl(this).WorkingArea;
        int nx = Math.Clamp(center.X - Width / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 8));
        int ny = Math.Clamp(center.Y - Height / 2, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - Height - 8));
        Location = new Point(nx, ny);
        BuildBackdrop();   // backdrop is size-dependent
        Invalidate();
    }

    // ---- layout (one source of truth for paint + hit-testing) ----
    private struct Lo
    {
        public Rectangle Restore, IconR, Min, Max, Close;
        public Rectangle Cover, Spectrum, Panel, Volume, Shuffle, Prev, Play, Next, Repeat, More;
        public Rectangle Seek, LeftTime, RightTime, Title, Artist;
    }

    private Lo Layout()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        bool c = _compact;
        var l = new Lo
        {
            Restore = new Rectangle(8, 7, 30, 28),
            IconR = new Rectangle(44, (TitleH - 18) / 2, 18, 18),
            Close = new Rectangle(w - 6 - 38, 6, 38, 30),
        };
        l.Max = new Rectangle(l.Close.Left - 38, 6, 38, 30);
        l.Min = new Rectangle(l.Max.Left - 38, 6, 38, 30);

        // sharp album cover — the hero — centred under the title bar
        int pm = c ? 12 : 14;
        int cov = c ? 150 : 218, covY = c ? 48 : 60;
        l.Cover = new Rectangle((w - cov) / 2, covY, cov, cov);

        // live spectrum band beneath the cover
        l.Spectrum = new Rectangle(pm, l.Cover.Bottom + 8, w - pm * 2, c ? 24 : 32);

        // centred track text beneath the spectrum
        int tY = l.Spectrum.Bottom + (c ? 6 : 8);
        l.Title = new Rectangle(18, tY, w - 36, c ? 24 : 30);
        l.Artist = new Rectangle(18, l.Title.Bottom + 1, w - 36, c ? 18 : 20);

        // floating frosted control panel, pinned to the bottom — two tight rows: controls then seek
        int ph = c ? 88 : 96;
        l.Panel = new Rectangle(pm, h - pm - ph, w - pm * 2, ph);

        // control row: 7 evenly-spaced controls, sitting in the top row of the panel
        int pad = c ? 22 : 26, cy = l.Panel.Top + (c ? 34 : 38);
        int x0 = l.Panel.Left + pad, x1 = l.Panel.Right - pad;
        float step = (x1 - x0) / 6f;
        Rectangle Box(int i, int s) => new((int)(x0 + i * step) - s / 2, cy - s / 2, s, s);
        l.Volume = Box(0, 32); l.Shuffle = Box(1, 32); l.Prev = Box(2, 32);
        l.Play = Box(3, 44); l.Next = Box(4, 32); l.Repeat = Box(5, 32); l.More = Box(6, 32);

        // seek row — just below the controls (tight gap)
        int sy = l.Panel.Bottom - (c ? 18 : 20);
        l.LeftTime = new Rectangle(l.Panel.Left + 12, sy - 8, 36, 18);
        l.RightTime = new Rectangle(l.Panel.Right - 12 - 36, sy - 8, 36, 18);
        l.Seek = new Rectangle(l.LeftTime.Right + 8, sy, l.RightTime.Left - 8 - (l.LeftTime.Right + 8), 5);
        return l;
    }

    // ---- interaction ----
    private void OnDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var l = Layout();
        if (l.Restore.Contains(e.Location) || l.Close.Contains(e.Location)) { ExpandRequested?.Invoke(); return; }
        if (l.Min.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
        if (l.Max.Contains(e.Location)) { SetCompact(!_compact); return; }
        if (l.Volume.Contains(e.Location)) { MuteRequested?.Invoke(); return; }
        if (l.Shuffle.Contains(e.Location)) { ShuffleRequested?.Invoke(); return; }
        if (l.Repeat.Contains(e.Location)) { RepeatRequested?.Invoke(); return; }
        if (l.More.Contains(e.Location)) { ShowMoreMenu(l.More); return; }
        if (l.Play.Contains(e.Location)) { PlayPauseRequested?.Invoke(); return; }
        if (l.Prev.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (l.Next.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (_track is not null && Inflate(l.Seek, 0, 11).Contains(e.Location)) { _scrubFrac = FracAt(l.Seek, e.X); Invalidate(); return; }
        StartWindowDrag(); // anywhere else: drag the whole window
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (_scrubFrac >= 0) { _scrubFrac = FracAt(l.Seek, e.X); Invalidate(SeekStrip()); return; }
        var h = l.Restore.Contains(e.Location) ? Hit.Restore
            : l.Min.Contains(e.Location) ? Hit.Min
            : l.Max.Contains(e.Location) ? Hit.Max
            : l.Close.Contains(e.Location) ? Hit.Close
            : l.Volume.Contains(e.Location) ? Hit.Volume
            : l.Shuffle.Contains(e.Location) ? Hit.Shuffle
            : l.Prev.Contains(e.Location) ? Hit.Prev
            : l.Play.Contains(e.Location) ? Hit.Play
            : l.Next.Contains(e.Location) ? Hit.Next
            : l.Repeat.Contains(e.Location) ? Hit.Repeat
            : l.More.Contains(e.Location) ? Hit.More
            : Hit.None;
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h != Hit.None ? Cursors.Hand : Cursors.Default;
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        if (_scrubFrac >= 0) { SeekRequested?.Invoke(_scrubFrac); _scrubFrac = -1; Invalidate(); }
    }

    private void ShowMoreMenu(Rectangle r)
    {
        var m = ThemedMenu.New();
        var anchor = RectangleToScreen(r);
        m.Items.Add("Equalizer…", null, (_, _) => EqualizerRequested?.Invoke(anchor));
        m.Items.Add("Pro features…", null, (_, _) => ProFeaturesRequested?.Invoke(anchor));
        m.Items.Add("Show full window", null, (_, _) => ExpandRequested?.Invoke());
        m.Show(this, new Point(r.Left, r.Bottom));
    }

    private double FracAt(Rectangle seek, int x) => Math.Clamp((x - seek.Left) / (double)Math.Max(1, seek.Width), 0, 1);
    private static Rectangle Inflate(Rectangle r, int dx, int dy) { var c = r; c.Inflate(dx, dy); return c; }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private void StartWindowDrag() { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } } // WM_NCLBUTTONDOWN, HTCAPTION

    // ---- paint ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width, h = ClientSize.Height;
        var l = Layout();
        bool idle = _track is null;

        // blurred-art ambient backdrop (or a themed gradient when there's no art)
        if (_backdrop is not null) g.DrawImage(_backdrop, 0, 0, w, h);
        else using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Blend(Theme.SidebarBg, Color.White, 0.06), Theme.Blend(Theme.SidebarBg, Color.Black, 0.22), 90f))
                g.FillRectangle(bg, 0, 0, w, h);

        // global dim so the sharp cover + white text always read over a bright blur, plus top/bottom scrims
        using (var wash = new SolidBrush(Color.FromArgb(74, 8, 9, 13))) g.FillRectangle(wash, 0, 0, w, h);
        using (var top = new LinearGradientBrush(new Rectangle(0, 0, w, TitleH + 20), Color.FromArgb(120, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
            g.FillRectangle(top, 0, 0, w, TitleH + 20);
        using (var bot = new LinearGradientBrush(new Rectangle(0, l.Cover.Bottom, w, h - l.Cover.Bottom + 1), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 90f))
            g.FillRectangle(bot, 0, l.Cover.Bottom, w, h - l.Cover.Bottom);
        // faint grain dithers the long dark gradients so they don't show 8-bit horizontal banding
        using (var grain = new TextureBrush(Noise()) { WrapMode = WrapMode.Tile }) g.FillRectangle(grain, 0, 0, w, h);

        // ---- title bar ----
        if (_iconBmp is not null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(_iconBmp, l.IconR); }
        TextRenderer.DrawText(g, "Mixtape", Theme.UiFont(9f), new Rectangle(l.IconR.Right + 8, 0, l.Min.Left - l.IconR.Right - 12, TitleH),
            Color.FromArgb(220, 240, 241, 244), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        CaptionBtn(g, l.Restore, GRestore, _hover == Hit.Restore, false, 14f);
        CaptionBtn(g, l.Min, GMin, _hover == Hit.Min, false, 10f);
        CaptionBtn(g, l.Max, _compact ? GMax : GRestoreDown, _hover == Hit.Max, false, 10f);   // grow ↔ shrink
        CaptionBtn(g, l.Close, GClose, _hover == Hit.Close, true, 10f);

        // ---- sharp album cover (the hero) ----
        DrawCover(g, l.Cover);

        // ---- live spectrum ----
        DrawSpectrum(g, l.Spectrum);

        // ---- centred track text ----
        var ctr = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        string title = idle ? "Nothing playing" : _track!.DisplayTitle;
        TextRenderer.DrawText(g, title, Theme.DisplayFont(_compact ? 15f : 18f), l.Title,
            idle ? Color.FromArgb(225, 235, 235, 237) : Color.White, ctr);
        string sub = idle ? "Pick a song to start" : (_track!.Artist ?? "");
        if (!string.IsNullOrWhiteSpace(sub))
            TextRenderer.DrawText(g, sub, Theme.UiFont(_compact ? 9.5f : 10.5f, FontStyle.Bold), l.Artist,
                Color.FromArgb(208, 224, 225, 230), ctr);

        // ---- control panel: frosted glass over the blurred art ----
        DrawFrostedPanel(g, l.Panel);

        Color on = Color.FromArgb(235, 240, 241, 244), dim = Color.FromArgb(120, 235, 238, 240);
        CtrlGlyph(g, l.Volume, _muted ? GMute : GVol, _muted ? Theme.Accent : on, _hover == Hit.Volume, 18f);
        CtrlGlyph(g, l.Shuffle, GShuffle, _shuffle ? Theme.Accent : on, _hover == Hit.Shuffle, 18f);
        CtrlGlyph(g, l.Prev, GPrev, idle ? dim : on, _hover == Hit.Prev, 17f);
        CtrlGlyph(g, l.Play, _playing ? GPause : GPlay, idle ? dim : Color.White, _hover == Hit.Play, 23f);
        CtrlGlyph(g, l.Next, GNext, idle ? dim : on, _hover == Hit.Next, 17f);
        CtrlGlyph(g, l.Repeat, _repeat == NowPlayingBar.RepeatMode.One ? GRepeatOne : GRepeatAll,
            _repeat != NowPlayingBar.RepeatMode.Off ? Theme.Accent : on, _hover == Hit.Repeat, 18f);
        CtrlGlyph(g, l.More, GMore, on, _hover == Hit.More, 18f);

        // ---- seek row ----
        double pos = _scrubFrac >= 0 ? _scrubFrac * _dur : DisplayPos();
        double frac = _scrubFrac >= 0 ? _scrubFrac : (_dur > 0 ? Math.Clamp(pos / _dur, 0, 1) : 0);
        DrawSeek(g, l.Seek, idle ? 0 : frac, !idle);
        Color tc = Color.FromArgb(200, 226, 227, 231);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(pos), Theme.UiFont(8f), l.LeftTime, tc, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(_dur), Theme.UiFont(8f), l.RightTime, tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // A row of centred, accent-tinted spectrum bars rising from the baseline (reacts to the live audio).
    private void DrawSpectrum(Graphics g, Rectangle r)
    {
        if (r.Height < 6 || r.Width < 30) return;
        int n = _spec.Length;
        float gap = 3f;
        float bw = (r.Width - (n - 1) * gap) / n;
        if (bw < 1.5f) return;
        float baseY = r.Bottom;
        float rad = Math.Min(2f, bw / 2f);
        for (int i = 0; i < n; i++)
        {
            float v = Math.Clamp(_spec[i], 0f, 1f);
            float bh = Math.Max(2f, v * r.Height);
            float x = r.X + i * (bw + gap);
            int a = (int)(120 + 110 * v);   // brighter as it peaks
            using var br = new SolidBrush(Color.FromArgb(a, Theme.AccentBright));
            using var p = Theme.RoundedRect(new RectangleF(x, baseY - bh, bw, bh), rad);
            g.FillPath(br, p);
        }
    }

    // The hero: the actual album cover drawn crisply, rounded, with a soft drop shadow + a hairline edge.
    private void DrawCover(Graphics g, Rectangle r)
    {
        int rad = Math.Max(8, (int)Math.Round(r.Width * Theme.TileFrac));
        for (int i = 4; i >= 1; i--)   // soft layered drop shadow
            using (var sh = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
            using (var sp = Theme.RoundedRect(new RectangleF(r.X - i, r.Y + i + 2, r.Width + i * 2, r.Height + i * 2), rad + i))
                g.FillPath(sh, sp);

        using var clip = Theme.RoundedRect(new RectangleF(r.X, r.Y, r.Width, r.Height), rad);
        var saved = g.Clip;
        g.SetClip(clip, CombineMode.Intersect);
        if (_cover is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_cover, r);
        }
        else using (var ph = new LinearGradientBrush(r, Theme.Blend(Theme.PanelBg, Color.White, 0.06), Theme.Blend(Theme.PanelBg, Color.Black, 0.28), 60f))
            g.FillRectangle(ph, r);
        g.Clip = saved;

        using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawPath(edge, clip);
    }

    private void DrawSeek(Graphics g, Rectangle track, double frac, bool knob)
    {
        var t = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1);
        using (var tb = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
        using (var tp = Theme.RoundedRect(t, t.Height / 2f)) g.FillPath(tb, tp);
        float fw = (float)(t.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(Theme.AccentBright))
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            float kx = t.X + fw, ky = t.Y + t.Height / 2f;
            using (var ks = new SolidBrush(Color.FromArgb(80, 0, 0, 0))) g.FillEllipse(ks, kx - 6f, ky - 5.5f, 12, 12);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx - 5.5f, ky - 5.5f, 11, 11);
        }
    }

    // Title-bar caption button: hover highlight (red for close, white otherwise) + a centred glyph.
    private void CaptionBtn(Graphics g, Rectangle r, string glyph, bool hover, bool close, float size)
    {
        if (hover)
            using (var hb = new SolidBrush(close ? Color.FromArgb(232, 17, 35) : Color.FromArgb(38, 255, 255, 255)))
                g.FillRectangle(hb, r);
        Glyph(g, r, glyph, hover ? Color.White : Color.FromArgb(220, 235, 236, 239), size);
    }

    // Control-panel glyph button: round hover halo + the glyph (accent when active).
    private void CtrlGlyph(Graphics g, Rectangle r, string glyph, Color c, bool hover, float size)
    {
        if (hover) { using var hb = new SolidBrush(Color.FromArgb(34, 255, 255, 255)); g.FillEllipse(hb, r); }
        Glyph(g, r, glyph, c, size);
    }

    // A frosted-glass material: the blurred art already shows beneath, so a translucent tint + a soft
    // top sheen + faint acrylic noise + a bright top edge read as a pane of frosted glass with elevation.
    private void DrawFrostedPanel(Graphics g, Rectangle p)
    {
        using (var sh = new SolidBrush(Color.FromArgb(75, 0, 0, 0)))
        using (var sp = Theme.RoundedRect(new RectangleF(p.X, p.Y + 4, p.Width, p.Height), 16)) g.FillPath(sh, sp);

        using var clip = Theme.RoundedRect(new RectangleF(p.X + 0.5f, p.Y + 0.5f, p.Width - 1, p.Height - 1), 16);
        var saved = g.Clip;
        g.SetClip(clip, CombineMode.Intersect);
        using (var tint = new SolidBrush(Color.FromArgb(120, 22, 23, 29))) g.FillRectangle(tint, p);          // dark glass (art shows through)
        using (var lum = new SolidBrush(Color.FromArgb(16, 255, 255, 255))) g.FillRectangle(lum, p);           // luminosity
        using (var sheen = new LinearGradientBrush(new Rectangle(p.X, p.Y, p.Width, Math.Max(1, p.Height * 2 / 3)), Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillRectangle(sheen, p.X, p.Y, p.Width, p.Height * 2 / 3);
        using (var nb = new TextureBrush(Noise()) { WrapMode = WrapMode.Tile }) g.FillRectangle(nb, p);        // subtle acrylic grain
        g.Clip = saved;

        using (var edge = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawPath(edge, clip);                  // bright glass edge
    }

    private static Bitmap? _noise;
    private static Bitmap Noise()
    {
        if (_noise is not null) return _noise;
        var bmp = new Bitmap(110, 110, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rnd = new Random(20260617);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int gray = rnd.Next(150, 225), a = rnd.Next(0, 12);
                bmp.SetPixel(x, y, Color.FromArgb(a, gray, gray, gray));
            }
        _noise = bmp;
        return _noise;
    }

    private void Glyph(Graphics g, Rectangle r, string glyph, Color c, float sizePx)
    {
        if (IconFont is null) return;
        using var f = new Font(IconFont, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var hint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.DrawString(glyph, f, b, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
        g.TextRenderingHint = hint;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim?.Cancel(); _cover?.Dispose(); _backdrop?.Dispose(); _iconBmp?.Dispose(); }
        base.Dispose(disposing);
    }
}
