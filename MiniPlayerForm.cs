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
    private Bitmap? _coverScaled;              // cover pre-scaled to the on-screen size (cheap per-frame blit during the pulse)
    private Bitmap? _backdrop;                 // (unused) legacy field
    private Color _tint = Theme.Accent;        // dominant colour of the current cover — tints the whole window
    private float _pulse;                      // 0..1 eased audio energy → the bloom "breathes" with the music
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
    // Cached fonts (Theme.UiFont/DisplayFont allocate a fresh GDI Font per call → don't do it ~30×/sec in OnPaint
    // while the bloom pulses). Title/artist have a normal + compact size; the rest are size-stable.
    private readonly Font _fName = Theme.UiFont(9f);
    private readonly Font _fTitleN = Theme.DisplayFont(17f, FontStyle.Bold), _fTitleC = Theme.DisplayFont(15f, FontStyle.Bold);
    private readonly Font _fArtistN = Theme.UiFont(10.5f, FontStyle.Bold), _fArtistC = Theme.UiFont(9.5f, FontStyle.Bold);
    private readonly Font _fTime = Theme.UiFont(8f);

    private enum Hit { None, Restore, Min, Max, Close, Volume, Shuffle, Prev, Play, Next, Repeat, More }
    private Hit _hover = Hit.None;

    private const int W = 340, H = 544, TitleH = 42;
    private static readonly Size NormalSize = new(W, H), CompactSize = new(300, 486);

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
        _coverScaled?.Dispose(); _coverScaled = null;
        _tint = _cover is not null ? DominantColor(_cover) : Theme.Accent;   // window tint follows the art
        Invalidate();
    }

    /// <summary>A representative, vivid colour for the cover — a saturation-weighted average over a tiny downsample,
    /// so the window tint reflects the art's mood rather than a muddy overall mean.</summary>
    private static Color DominantColor(Bitmap cover)
    {
        try
        {
            using var sm = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(sm)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(cover, 0, 0, 20, 20); }
            double r = 0, gg = 0, b = 0, wsum = 0;
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 20; x++)
                {
                    var p = sm.GetPixel(x, y);
                    float mx = Math.Max(p.R, Math.Max(p.G, p.B)) / 255f, mn = Math.Min(p.R, Math.Min(p.G, p.B)) / 255f;
                    float sat = mx <= 0.001f ? 0 : (mx - mn) / mx;
                    double w = sat * mx + 0.04;   // vivid pixels dominate; small base keeps an all-grey cover its grey
                    r += p.R * w; gg += p.G * w; b += p.B * w; wsum += w;
                }
            if (wsum <= 0) return Theme.Accent;
            return Color.FromArgb((int)(r / wsum), (int)(gg / wsum), (int)(b / wsum));
        }
        catch { return Theme.Accent; }
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

    // The Clean Card layout draws on a flat themed background (no blurred-cover wash), so there's nothing to
    // build — kept as a no-op so the SetTrack/SetCompact callers don't need to change.
    private void BuildBackdrop() { _backdrop?.Dispose(); _backdrop = null; }

    private void StartAnim()
    {
        if (_anim is { IsRunning: true } || !Anim.MotionEnabled) return;
        int tick = 0;
        _anim = Anim.Run(1_000_000_000, _ =>
        {
            if (IsDisposed) return;
            UpdateSpectrum();
            UpdatePulse();
            if ((++tick & 1) == 0) Invalidate();   // full repaint: the tint bloom breathes + the seek bar advances
        }, null, Easings.Linear);
    }
    private void StopAnim() { _anim?.Cancel(); _anim = null; Array.Clear(_spec); _pulse = 0; if (!IsDisposed && IsHandleCreated) Invalidate(); }

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

    // Beat-ish energy from the low / low-mid bands, eased with a fast attack + slow decay so the bloom "pumps".
    private void UpdatePulse()
    {
        int n = Math.Min(10, _spec.Length);
        float e = 0; for (int i = 0; i < n; i++) e += _spec[i];
        e = Math.Clamp(e / Math.Max(1, n) * 1.5f, 0f, 1f);
        _pulse += (e - _pulse) * (e > _pulse ? 0.5f : 0.12f);
    }

    /// <summary>Render-only: inject a static spectrum so a screenshot shows a representative pulse (no live audio).</summary>
    public void DebugSpectrum(float[] b) { for (int i = 0; i < _spec.Length; i++) _spec[i] = i < b.Length ? b[i] : 0f; for (int i = 0; i < 8; i++) UpdatePulse(); Invalidate(); }
    private Rectangle SeekStrip() { var l = Layout(); return new Rectangle(0, l.Seek.Y - 14, ClientSize.Width, 30); }

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
        int m = c ? 18 : 24;   // side margin
        var l = new Lo
        {
            Restore = new Rectangle(8, 7, 30, 28),
            IconR = new Rectangle(44, (TitleH - 18) / 2, 18, 18),
            Close = new Rectangle(w - 6 - 38, 6, 38, 30),
        };
        l.Max = new Rectangle(l.Close.Left - 38, 6, 38, 30);
        l.Min = new Rectangle(l.Max.Left - 38, 6, 38, 30);

        // album cover — the hero — fills the width under the title bar
        int covY = TitleH + (c ? 4 : 6), cov = w - 2 * m;
        l.Cover = new Rectangle(m, covY, cov, cov);

        // left-aligned title + artist beneath the cover
        int tY = l.Cover.Bottom + (c ? 14 : 18);
        l.Title = new Rectangle(m, tY, w - 2 * m, c ? 24 : 28);
        l.Artist = new Rectangle(m, l.Title.Bottom + 1, w - 2 * m, c ? 16 : 20);

        // seek bar (full width) + the times tucked just below its ends
        int sy = l.Artist.Bottom + (c ? 12 : 16);
        l.Seek = new Rectangle(m, sy, w - 2 * m, 4);
        l.LeftTime = new Rectangle(m, sy + 8, 64, 14);
        l.RightTime = new Rectangle(w - m - 64, sy + 8, 64, 14);

        // transport row, anchored a fixed margin off the BOTTOM. NON-uniform spacing about the centre: the
        // prominent play disc needs clear breathing room from prev/next (even 7-up spacing crammed them against
        // it), with shuffle/repeat a step further and volume/more out at the edges as secondary controls.
        int play = c ? 44 : 50;
        int cy = h - (c ? 22 : 26) - play / 2;
        int cx = w / 2;
        int o1 = c ? 44 : 49, o2 = c ? 80 : 90, o3 = c ? 116 : 132;   // prev/next · shuffle/repeat · volume/more
        Rectangle Box(int center, int s) => new(center - s / 2, cy - s / 2, s, s);
        l.Play = Box(cx, play);
        l.Prev = Box(cx - o1, 32); l.Next = Box(cx + o1, 32);
        l.Shuffle = Box(cx - o2, 30); l.Repeat = Box(cx + o2, 30);
        l.Volume = Box(cx - o3, 30); l.More = Box(cx + o3, 30);

        l.Spectrum = Rectangle.Empty; l.Panel = Rectangle.Empty;   // dropped in the Clean Card layout
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
        m.Items.Add(Loc.T("Equalizer…"), null, (_, _) => EqualizerRequested?.Invoke(anchor));
        m.Items.Add(Loc.T("Pro features…"), null, (_, _) => ProFeaturesRequested?.Invoke(anchor));
        m.Items.Add(Loc.T("Show full window"), null, (_, _) => ExpandRequested?.Invoke());
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

        // adaptive background: a dark wash of the cover's dominant colour, with a soft bloom behind the art that
        // breathes with the music (see UpdatePulse). Idle / art-less falls back to the accent tint.
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Blend(_tint, Color.Black, 0.58), Theme.Blend(_tint, Color.Black, 0.88), 90f))
            g.FillRectangle(bg, 0, 0, w, h);
        DrawBloom(g, l.Cover);
        using (var grain = new TextureBrush(Noise()) { WrapMode = WrapMode.Tile }) g.FillRectangle(grain, 0, 0, w, h);

        // ---- title bar ----
        if (_iconBmp is not null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(_iconBmp, l.IconR); }
        TextRenderer.DrawText(g, "Mixtape", _fName, new Rectangle(l.IconR.Right + 8, 0, l.Min.Left - l.IconR.Right - 12, TitleH),
            Color.FromArgb(205, 232, 233, 237), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        CaptionBtn(g, l.Restore, GRestore, _hover == Hit.Restore, false, 14f);
        CaptionBtn(g, l.Min, GMin, _hover == Hit.Min, false, 10f);
        CaptionBtn(g, l.Max, _compact ? GMax : GRestoreDown, _hover == Hit.Max, false, 10f);   // grow ↔ shrink
        CaptionBtn(g, l.Close, GClose, _hover == Hit.Close, true, 10f);

        // ---- album cover ----
        DrawCover(g, l.Cover);

        // ---- left-aligned title + artist ----
        var left = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        string title = idle ? Loc.T("Nothing playing") : _track!.DisplayTitle;
        TextRenderer.DrawText(g, title, _compact ? _fTitleC : _fTitleN, l.Title,
            idle ? Color.FromArgb(224, 235, 235, 237) : Color.White, left);
        string sub = idle ? Loc.T("Pick a song to start") : (_track!.Artist ?? "");
        if (!string.IsNullOrWhiteSpace(sub))
            TextRenderer.DrawText(g, sub, _compact ? _fArtistC : _fArtistN, l.Artist,
                Color.FromArgb(188, 218, 220, 226), left);

        // ---- seek + times ----
        double pos = _scrubFrac >= 0 ? _scrubFrac * _dur : DisplayPos();
        double frac = _scrubFrac >= 0 ? _scrubFrac : (_dur > 0 ? Math.Clamp(pos / _dur, 0, 1) : 0);
        DrawSeek(g, l.Seek, idle ? 0 : frac, !idle);
        Color tc = Color.FromArgb(185, 224, 226, 231);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(pos), _fTime, l.LeftTime, tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(_dur), _fTime, l.RightTime, tc, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // ---- transport: flat glyphs around a filled accent play button ----
        Color on = Color.FromArgb(235, 240, 241, 244), dim = Color.FromArgb(110, 235, 238, 240);
        CtrlGlyph(g, l.Volume, _muted ? GMute : GVol, _muted ? Theme.Accent : on, _hover == Hit.Volume, 17f);
        CtrlGlyph(g, l.Shuffle, GShuffle, _shuffle ? Theme.Accent : on, _hover == Hit.Shuffle, 17f);
        CtrlGlyph(g, l.Prev, GPrev, idle ? dim : on, _hover == Hit.Prev, 18f);
        DrawPlayButton(g, l.Play, idle);
        CtrlGlyph(g, l.Next, GNext, idle ? dim : on, _hover == Hit.Next, 18f);
        CtrlGlyph(g, l.Repeat, _repeat == NowPlayingBar.RepeatMode.One ? GRepeatOne : GRepeatAll,
            _repeat != NowPlayingBar.RepeatMode.Off ? Theme.Accent : on, _hover == Hit.Repeat, 17f);
        CtrlGlyph(g, l.More, GMore, on, _hover == Hit.More, 17f);
    }

    // A soft radial bloom of the cover's tint, centred on the art and pulsing with the beat (alpha + a little
    // radius). The opaque cover occludes the bright centre, so it reads as a halo glowing out from the artwork.
    private void DrawBloom(Graphics g, Rectangle cover)
    {
        float e = Math.Clamp(_pulse, 0f, 1f);
        int a = (int)(22 + 98 * e);
        if (a <= 3) return;
        float cx = cover.X + cover.Width / 2f, cy = cover.Y + cover.Height * 0.58f;
        float rad = cover.Width * (1.02f + 0.10f * e);
        using var path = new GraphicsPath();
        path.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
        using var pgb = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(a, Theme.Blend(_tint, Color.White, 0.16)),
            SurroundColors = new[] { Color.FromArgb(0, _tint) },
            CenterPoint = new PointF(cx, cy),
            FocusScales = new PointF(0.18f, 0.18f),
        };
        g.FillEllipse(pgb, cx - rad, cy - rad, rad * 2, rad * 2);
    }

    // The hero transport control: a filled accent disc with the play/pause glyph, a soft drop shadow, and a
    // subtle lighten on hover. Muted (not dimmed-flat) when idle so it still reads as the primary action.
    private void DrawPlayButton(Graphics g, Rectangle r, bool idle)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(sh, rf.X, rf.Y + 2.5f, rf.Width, rf.Height);
        Color fill = idle ? Theme.Blend(Theme.Accent, Theme.SidebarBg, 0.45)
                          : _hover == Hit.Play ? Theme.Blend(Theme.Accent, Color.White, 0.14) : Theme.Accent;
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, rf);

        // Draw play/pause as VECTORS (not a font glyph — its metrics rendered the triangle off-centre + outlined).
        Color ink = idle ? Color.FromArgb(225, 255, 255, 255) : Theme.OnAccent;
        using var ib = new SolidBrush(ink);
        float cx = rf.X + rf.Width / 2f, cy = rf.Y + rf.Height / 2f;
        if (_playing)
        {
            float bw = rf.Width * 0.13f, bh = rf.Height * 0.36f, gap = rf.Width * 0.12f, rad = bw * 0.35f;
            float x0 = cx - (2 * bw + gap) / 2f;
            using (var p1 = Theme.RoundedRect(new RectangleF(x0, cy - bh / 2f, bw, bh), rad)) g.FillPath(ib, p1);
            using (var p2 = Theme.RoundedRect(new RectangleF(x0 + bw + gap, cy - bh / 2f, bw, bh), rad)) g.FillPath(ib, p2);
        }
        else
        {
            // A right-pointing triangle centred on its CENTROID (1/3 from the base) so it reads optically centred.
            float s = rf.Height * 0.22f, tw = rf.Height * 0.40f, left = cx - tw / 3f;
            g.FillPolygon(ib, new[] { new PointF(left, cy - s), new PointF(left, cy + s), new PointF(left + tw, cy) });
        }
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
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
            // Pre-scale once and blit (the window now repaints ~30fps for the pulse — a per-frame bicubic resize
            // of the cover would be wasteful).
            if (_coverScaled is null || _coverScaled.Width != r.Width || _coverScaled.Height != r.Height)
            {
                _coverScaled?.Dispose();
                _coverScaled = new Bitmap(r.Width, r.Height);
                using var cg = Graphics.FromImage(_coverScaled);
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic; cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.DrawImage(_cover, 0, 0, r.Width, r.Height);
            }
            g.DrawImage(_coverScaled, r.Location);
        }
        else
        {
            using (var ph = new LinearGradientBrush(r, Theme.Blend(Theme.PanelBg, Color.White, 0.06), Theme.Blend(Theme.PanelBg, Color.Black, 0.28), 60f))
                g.FillRectangle(ph, r);
            float ns = r.Width * 0.28f;   // a soft music-note mark instead of an empty square when idle
            Theme.DrawNote(g, new RectangleF(r.X + (r.Width - ns) / 2f, r.Y + (r.Height - ns) / 2f, ns, ns), Color.FromArgb(64, 255, 255, 255));
        }
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
        if (disposing) { _anim?.Cancel(); _cover?.Dispose(); _coverScaled?.Dispose(); _backdrop?.Dispose(); _iconBmp?.Dispose(); _fName.Dispose(); _fTitleN.Dispose(); _fTitleC.Dispose(); _fArtistN.Dispose(); _fArtistC.Dispose(); _fTime.Dispose(); }
        base.Dispose(disposing);
    }
}
