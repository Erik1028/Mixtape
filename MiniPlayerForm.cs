using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The mini player: the window's top deck, DETACHED. The same transport (shuffle · prev · play · next · repeat),
/// the same now-playing card (cover, two lines, elapsed over total, the seek line), the speaker (plus the volume
/// slider once the strip is wide enough) and a "···" for the rest (Up Next, lyrics, equalizer, Pro features, back
/// to the full window). No caption bar: the strip drags anywhere that is not a control, and its left/right edges
/// resize it. It mirrors and drives the main window's single audio engine through <see cref="NowPlayingBar"/>'s
/// facade — it never plays anything itself. The ×, Esc and the cover all return to the full window (the mini never
/// quits the app on its own).
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
    public event Action<Rectangle>? QueueRequested;         // open the Up Next popover
    public event Action<Rectangle>? LyricsRequested;        // open the lyrics popover
    public event Action? CoverClicked;               // the card's cover: back to the full window, at the playing song
    public event Action? ExpandRequested;            // return to the full window

    private Track? _track;
    private Bitmap? _cover, _coverPrev;             // the card's cover + the outgoing one during the dissolve
    private float _coverFade = 1f;
    private Tween? _coverTween;
    /// <summary>Mirrors the deck's total-time toggle (the host keeps both in step).</summary>
    public bool ShowRemaining { get; set; }
    private Color _tint = Theme.Accent;             // seek fill + cover bars follow the cover's dominant colour (as on the deck)
    private bool _playing, _shuffle, _muted;
    private NowPlayingBar.RepeatMode _repeat;
    private double _dur, _vol = 1;
    private double _posBase;                        // engine position at the last push
    private readonly Stopwatch _sw = new();         // interpolates between the ~5 Hz pushes so the seek line moves smoothly
    private Tween? _anim;                           // ~30 fps repaint while playing: the seek line + the cover bars
    private double _scrubFrac = -1;                 // while dragging the seek line
    private float _playMorph;                       // play glyph: 0 = triangle, 1 = pause bars
    private Tween? _playTween;
    private float _seekKnobR = 4f, _volKnobR = 5f;  // grab knobs grow on hover/drag (the deck's tween)
    private Tween? _knobTween;
    private double _eqPhase;                        // the cover's "now playing" bars
    private readonly float[] _viz = new float[4], _vizTmp = new float[4];
    private bool _vizFrozen;                        // render harness: keep the injected bars
    private readonly EqBarsPainter _eqBars = new();
    public Func<float[], bool>? SpectrumProvider;   // host fills 0..1 spectrum bands; false when silent/paused
    private Bitmap? _wall;                          // the deck's wallpaper strip at the current width
    private int _wallRev = -1;
    // Fonts as on the deck (Theme.UiFont allocates a GDI Font per call — never inside OnPaint).
    private readonly Font _fTitle = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);
    private readonly Font _fSub = Theme.UiFont(8.75f);
    private readonly Font _fTime = Theme.UiFont(8f);

    private enum Hit { None, Shuffle, Prev, Play, Next, Repeat, Cover, Seek, Speaker, Vol, More, Close }
    private enum Drag { None, Seek, Volume }
    private Hit _hover = Hit.None;
    private Drag _drag = Drag.None;

    public const int H = NowPlayingBar.TopH;                    // the deck's height
    private const int Pad = 14, CardMin = 300, CardMax = 520;   // the deck's card bounds
    private const int VolCost = 84 + 10;                        // the volume slider + its gap — shown once the strip has this much to spare
    private const int Grip = 6;                                 // px at the left/right edge that resizes the strip
    private const int WallH = 700;                              // the wallpaper is painted at a window's height; the strip is its top
    /// <summary>The transport, the card at its minimum, "···", the speaker and ×.</summary>
    public const int MinW = Pad + 176 + 16 + CardMin + 16 + 24 + 12 + 20 + 10 + 24 + 12;
    /// <summary>The volume slider shown and the card at its widest.</summary>
    public const int MaxW = MinW + VolCost + (CardMax - CardMin);

    public MiniPlayerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(MinW, H);
        MinimumSize = new Size(MinW, H);
        MaximumSize = new Size(MaxW, H);
        BackColor = Theme.SidebarBg;
        Text = "Mixtape — Mini Player";
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        try { if (Environment.ProcessPath is string p) Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); } catch { }

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { if (_hover != Hit.None) { _hover = Hit.None; RetargetKnobs(); Invalidate(); } };
        DoubleClick += (_, _) => { if (HitAt(PointToClient(Cursor.Position)) == Hit.None) ExpandRequested?.Invoke(); };
        KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Escape: ExpandRequested?.Invoke(); break;
                case Keys.Space: PlayPauseRequested?.Invoke(); e.Handled = true; break;
                case Keys.Right: NextRequested?.Invoke(); break;
                case Keys.Left: PrevRequested?.Invoke(); break;
                case Keys.Up: NudgeVolume(+0.06); break;
                case Keys.Down: NudgeVolume(-0.06); break;
            }
        };
    }

    // ---- host → mini state ----

    /// <summary>Set the now-playing track + cover (TAKES OWNERSHIP of <paramref name="cover"/>); the card
    /// cross-dissolves to the new art like the deck's.</summary>
    public void SetTrack(Track? track, Bitmap? cover)
    {
        _track = track;
        _coverTween?.Cancel(); _coverTween = null;
        _coverPrev?.Dispose();
        _coverPrev = _cover; _cover = cover;
        _tint = NowPlayingBar.AccentTintFor(_cover);
        if (_coverPrev is null || !Visible || !Anim.MotionEnabled)
        {
            _coverPrev?.Dispose(); _coverPrev = null; _coverFade = 1f;   // nothing to dissolve from
        }
        else
        {
            _coverFade = 0f;
            _coverTween = Anim.Run(220, v => { _coverFade = (float)v; if (!IsDisposed) Invalidate(); },
                () => { _coverTween = null; _coverPrev?.Dispose(); _coverPrev = null; _coverFade = 1f; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
        }
        Invalidate();
    }

    /// <summary>Push live playback state (cheap; called on every engine tick + state change).</summary>
    public void SetProgress(bool playing, double posSec, double durSec, double volume, bool muted, bool shuffle, NowPlayingBar.RepeatMode repeat)
    {
        if (playing != _playing) { _playing = playing; RetargetPlay(); }
        _dur = durSec; _vol = volume; _muted = muted; _shuffle = shuffle; _repeat = repeat;
        _posBase = posSec; _sw.Restart();
        if (playing && Visible) StartAnim(); else StopAnim();
        Invalidate();
    }

    private double DisplayPos()
    {
        double p = _posBase + (_playing ? _sw.Elapsed.TotalSeconds : 0);
        return _dur > 0 ? Math.Min(_dur, Math.Max(0, p)) : Math.Max(0, p);
    }

    // The play glyph morphs between the triangle and the pause bars (the deck's 160 ms).
    private void RetargetPlay()
    {
        float to = _playing ? 1f : 0f;
        _playTween?.Cancel(); _playTween = null;
        if (!Anim.MotionEnabled || !Visible) { _playMorph = to; return; }
        float from = _playMorph;
        _playTween = Anim.Run(160, v => { _playMorph = from + (to - from) * (float)v; if (!IsDisposed) Invalidate(); },
            () => { _playTween = null; _playMorph = to; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
    }

    private void StartAnim()
    {
        if (_anim is { IsRunning: true } || !Anim.MotionEnabled) return;
        int tick = 0;
        _anim = Anim.Run(1_000_000_000, _ =>
        {
            if (IsDisposed) return;
            _eqPhase += 0.22;
            UpdateViz();
            if ((++tick & 1) == 0) Invalidate(Layout().Card);   // the seek line advances + the cover bars move — only the card repaints
        }, null, Easings.Linear);
    }
    private void StopAnim() { _anim?.Cancel(); _anim = null; if (!_vizFrozen) Array.Clear(_viz); if (!IsDisposed && IsHandleCreated) Invalidate(); }

    // Ease the cover bars toward the host's live bands (fast attack, slow decay); they settle to the gentle baseline when silent.
    private void UpdateViz()
    {
        if (_vizFrozen) return;
        bool live = SpectrumProvider?.Invoke(_vizTmp) ?? false;
        for (int i = 0; i < _viz.Length; i++)
        {
            float target = live ? _vizTmp[i] : 0f;
            _viz[i] += (target - _viz[i]) * (target > _viz[i] ? 0.5f : 0.16f);
        }
    }

    /// <summary>Render harness only: a fixed set of cover bars + a hovered control, so a screenshot shows the live look.</summary>
    public void DebugState(float[] bars, string? hover)
    {
        for (int i = 0; i < _viz.Length; i++) _viz[i] = i < bars.Length ? bars[i] : 0f;
        _vizFrozen = true;
        _hover = hover switch { "seek" => Hit.Seek, "cover" => Hit.Cover, "play" => Hit.Play, "vol" => Hit.Vol, "more" => Hit.More, "close" => Hit.Close, _ => Hit.None };
        if (_hover == Hit.Seek) _seekKnobR = 6f;
        if (_hover == Hit.Vol) _volKnobR = 8f;
        Invalidate();
    }

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
        NudgeVolume(e.Delta > 0 ? 0.06 : -0.06);
    }

    private void NudgeVolume(double d)
    {
        double v = Math.Clamp((_muted ? 0 : _vol) + d, 0, 1);
        _vol = v; _muted = v <= 0.001;
        VolumeRequested?.Invoke(v);
        Invalidate();
    }

    // ---- layout (one source of truth for paint + hit-testing): the deck's own spacing, left → right ----
    private struct Lo
    {
        public Rectangle Shuffle, Prev, Play, Next, Repeat, Card, More, Speaker, Vol, Close;
        public bool ShowVol;
        public NowPlayingBar.CardGeom Cg;
    }

    private Lo Layout()
    {
        int w = ClientSize.Width, cy = H / 2;
        var l = new Lo();
        int x = Pad;
        l.Shuffle = new Rectangle(x, cy - 13, 26, 26); x = l.Shuffle.Right + 8;
        l.Prev = new Rectangle(x, cy - 15, 30, 30); x = l.Prev.Right + 6;
        l.Play = new Rectangle(x, cy - 18, 36, 36); x = l.Play.Right + 6;
        l.Next = new Rectangle(x, cy - 15, 30, 30); x = l.Next.Right + 8;
        l.Repeat = new Rectangle(x, cy - 13, 26, 26); x = l.Repeat.Right;

        // Right cluster, right → left: ×, the volume (once the strip has the room), the speaker, "···".
        int rc = w - 12;
        l.Close = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Close.Left - 10;
        l.ShowVol = w - MinW >= VolCost;
        if (l.ShowVol) { l.Vol = new Rectangle(rc - 84, cy - 2, 84, 4); rc = l.Vol.Left - 10; }
        l.Speaker = new Rectangle(rc - 20, cy - 11, 20, 22); rc = l.Speaker.Left - 12;
        l.More = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.More.Left;

        // The card: centred in the free span, capped so it stays a card and not a banner.
        int spanL = x + 16, spanR = rc - 16;
        int cardW = Math.Clamp(spanR - spanL, CardMin, CardMax);
        l.Card = new Rectangle(spanL + (spanR - spanL - cardW) / 2, NowPlayingBar.CardY, cardW, NowPlayingBar.CardH);
        l.Cg = NowPlayingBar.LayoutCard(l.Card);
        return l;
    }

    private Hit HitAt(Point p)
    {
        var l = Layout();
        if (l.Close.Contains(p)) return Hit.Close;
        if (l.More.Contains(p)) return Hit.More;
        if (l.Speaker.Contains(p)) return Hit.Speaker;
        if (l.ShowVol && Inflate(l.Vol, 0, 9).Contains(p)) return Hit.Vol;
        if (l.Shuffle.Contains(p)) return Hit.Shuffle;
        if (l.Repeat.Contains(p)) return Hit.Repeat;
        if (_track is null) return Hit.None;                 // the transport, cover and seek are inert with nothing loaded
        if (l.Cg.Cover.Contains(p)) return Hit.Cover;
        if (Inflate(l.Cg.Seek, 0, 10).Contains(p)) return Hit.Seek;
        if (l.Play.Contains(p)) return Hit.Play;
        if (l.Prev.Contains(p)) return Hit.Prev;
        if (l.Next.Contains(p)) return Hit.Next;
        return Hit.None;
    }

    // ---- interaction ----
    private void OnDown(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (e.Button == MouseButtons.Right) { if (l.Card.Contains(e.Location)) ShowMoreMenu(l.More); return; }
        if (e.Button != MouseButtons.Left) return;
        int edge = ResizeEdge(e.Location); if (edge != 0) { StartWindowResize(edge); return; }
        switch (HitAt(e.Location))
        {
            case Hit.Close: ExpandRequested?.Invoke(); return;
            case Hit.More: ShowMoreMenu(l.More); return;
            case Hit.Speaker: MuteRequested?.Invoke(); return;
            case Hit.Vol: _drag = Drag.Volume; RetargetKnobs(); SetVolumeFromX(l.Vol, e.X); return;
            case Hit.Shuffle: ShuffleRequested?.Invoke(); return;
            case Hit.Repeat: RepeatRequested?.Invoke(); return;
            case Hit.Cover: CoverClicked?.Invoke(); return;
            case Hit.Seek: _drag = Drag.Seek; RetargetKnobs(); _scrubFrac = FracAt(l.Cg.Seek, e.X); Invalidate(); return;
            case Hit.Play: PlayPauseRequested?.Invoke(); return;
            case Hit.Prev: PrevRequested?.Invoke(); return;
            case Hit.Next: NextRequested?.Invoke(); return;
        }
        StartWindowDrag();   // anywhere else — the wallpaper, the card's text — moves the strip
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (_drag == Drag.Volume) { SetVolumeFromX(l.Vol, e.X); return; }
        if (_drag == Drag.Seek) { _scrubFrac = FracAt(l.Cg.Seek, e.X); Invalidate(); return; }
        if (ResizeEdge(e.Location) != 0)   // over a resize edge → the sizing cursor (the edges sit in the margins, clear of controls)
        {
            Cursor = Cursors.SizeWE;
            if (_hover != Hit.None) { _hover = Hit.None; RetargetKnobs(); Invalidate(); }
            return;
        }
        var h = HitAt(e.Location);
        Cursor = h == Hit.Cover ? Cursors.Hand : Cursors.Default;
        if (h != _hover) { _hover = h; RetargetKnobs(); Invalidate(); }
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        if (_drag == Drag.Seek && _scrubFrac >= 0)
        {
            SeekRequested?.Invoke(_scrubFrac);
            _posBase = _scrubFrac * _dur; _sw.Restart();   // show the new position at once (the host's next push confirms it)
        }
        _scrubFrac = -1; _drag = Drag.None;
        RetargetKnobs(); Invalidate();
    }

    /// <summary>Smoothly grow/shrink the seek + volume grab-knobs toward their hover/drag radius (the deck's 4→6 / 5→8 px, 120 ms).</summary>
    private void RetargetKnobs()
    {
        float seekTo = (_hover == Hit.Seek || _drag == Drag.Seek) ? 6f : 4f;
        float volTo = (_hover == Hit.Vol || _drag == Drag.Volume) ? 8f : 5f;
        if (Math.Abs(seekTo - _seekKnobR) < 0.1f && Math.Abs(volTo - _volKnobR) < 0.1f) return;
        _knobTween?.Cancel();
        float seekFrom = _seekKnobR, volFrom = _volKnobR;
        if (!Anim.MotionEnabled) { _seekKnobR = seekTo; _volKnobR = volTo; Invalidate(); return; }
        _knobTween = Anim.Run(120, v => { float f = (float)v; _seekKnobR = seekFrom + (seekTo - seekFrom) * f; _volKnobR = volFrom + (volTo - volFrom) * f; if (!IsDisposed) Invalidate(); },
            () => { _seekKnobR = seekTo; _volKnobR = volTo; _knobTween = null; }, Easings.OutCubic);
    }

    private void SetVolumeFromX(Rectangle vol, int x)
    {
        _vol = Math.Clamp((x - vol.Left) / (double)Math.Max(1, vol.Width), 0, 1);
        _muted = _vol <= 0.001;
        VolumeRequested?.Invoke(_vol);
        Invalidate();
    }

    /// <summary>"···": what the strip has no room for. The popovers anchor to the strip's full height at the button,
    /// so they open clear of it (the strip is topmost; a flyout under it would be covered).</summary>
    private void ShowMoreMenu(Rectangle r)
    {
        var m = ThemedMenu.New();
        var strip = RectangleToScreen(ClientRectangle);
        var anchor = new Rectangle(RectangleToScreen(r).X, strip.Y, r.Width, strip.Height);
        m.Items.Add(Loc.T("Up Next…"), null, (_, _) => QueueRequested?.Invoke(anchor));
        m.Items.Add(Loc.T("Lyrics…"), null, (_, _) => LyricsRequested?.Invoke(anchor));
        m.Items.Add(Loc.T("Equalizer…"), null, (_, _) => EqualizerRequested?.Invoke(anchor));
        m.Items.Add(Loc.T("Pro features…"), null, (_, _) => ProFeaturesRequested?.Invoke(anchor));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Loc.T("Show full window"), null, (_, _) => ExpandRequested?.Invoke());
        m.Show(this, new Point(r.Left, r.Bottom + 6));
    }

    private static double FracAt(Rectangle seek, int x) => Math.Clamp((x - seek.Left) / (double)Math.Max(1, seek.Width), 0, 1);
    private static Rectangle Inflate(Rectangle r, int dx, int dy) { var c = r; c.Inflate(dx, dy); return c; }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private void StartWindowDrag() { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } }      // WM_NCLBUTTONDOWN, HTCAPTION
    private void StartWindowResize(int ht) { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)ht, IntPtr.Zero); } catch { } }   // same trick, with a resize HT code

    /// <summary>The resize handle (HT* code) under <paramref name="p"/>, or 0: the left and right edges only — the strip's
    /// height is the deck's (MinimumSize/MaximumSize pin it).</summary>
    private int ResizeEdge(Point p)
    {
        if (p.X <= Grip) return 10;                      // HTLEFT
        if (p.X >= ClientSize.Width - Grip) return 11;   // HTRIGHT
        return 0;
    }

    // ---- paint ----

    /// <summary>The deck sits on the main window's wallpaper: paint the same wallpaper at a window's height and keep its
    /// top strip, cached per width + theme revision.</summary>
    private void EnsureWall()
    {
        int w = ClientSize.Width;
        if (w <= 0) return;
        if (_wall is { } b && b.Width == w && _wallRev == Theme.Revision) return;
        _wall?.Dispose();
        _wall = new Bitmap(w, H);
        using var g = Graphics.FromImage(_wall);
        Theme.PaintWallpaper(g, new Rectangle(0, 0, w, WallH));
        _wallRev = Theme.Revision;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        EnsureWall();
        if (_wall is not null) Theme.BlitExact(g, _wall, new Rectangle(0, 0, _wall.Width, _wall.Height));
        else Theme.PaintWallpaper(g, ClientRectangle);

        var l = Layout();
        bool idle = _track is null;

        // transport (dimmed + inert when idle); shuffle/repeat are modes — always live
        NowPlayingBar.DrawShuffle(g, l.Shuffle, _shuffle, _hover == Hit.Shuffle);
        NowPlayingBar.DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, NowPlayingBar.GlyphPrevL, idle);
        NowPlayingBar.DrawPlayDisc(g, l.Play, _hover == Hit.Play, idle, _playMorph);
        NowPlayingBar.DrawCircleGlyph(g, l.Next, _hover == Hit.Next, NowPlayingBar.GlyphNextL, idle);
        NowPlayingBar.DrawRepeat(g, l.Repeat, _repeat, _hover == Hit.Repeat);

        // the card — the deck's, verbatim
        var st = new NowPlayingBar.CardState
        {
            Track = _track, Cover = _cover, CoverPrev = _coverPrev, CoverFade = _coverFade, Playing = _playing,
            CoverHover = _hover == Hit.Cover, SeekHot = _hover == Hit.Seek || _drag == Drag.Seek, ScrubFrac = _scrubFrac,
            Pos = DisplayPos(), Dur = _dur, Tint = _tint, KnobR = _seekKnobR, EqPhase = _eqPhase, Viz = _viz,
            Remaining = ShowRemaining,
        };
        NowPlayingBar.DrawCard(g, l.Cg, st, _fTitle, _fSub, _fTime, _eqBars);

        // utilities
        NowPlayingBar.DrawOverflowGlyph(g, l.More, _hover == Hit.More);
        NowPlayingBar.DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        if (l.ShowVol) NowPlayingBar.DrawSlider(g, l.Vol, _muted ? 0 : _vol, true, Theme.Accent, _volKnobR, _hover == Hit.Vol || _drag == Drag.Volume, Color.FromArgb(38, 255, 255, 255), lift: false);
        DrawClose(g, l.Close, _hover == Hit.Close);
    }

    // ×: back to the full window. The main window's close glyph, on a red chip while hovered (Windows' own cue).
    private static void DrawClose(Graphics g, Rectangle r, bool hover)
    {
        if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(232, 17, 35));
            using var hp = Theme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.RadControl);
            g.FillPath(hb, hp);
        }
        using var pen = new Pen(hover ? Color.White : Color.FromArgb(218, 222, 226), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 5f;
        g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
        g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim?.Cancel(); _coverTween?.Cancel(); _playTween?.Cancel(); _knobTween?.Cancel();
            _cover?.Dispose(); _coverPrev?.Dispose(); _wall?.Dispose(); _eqBars.Dispose();
            _fTitle.Dispose(); _fSub.Dispose(); _fTime.Dispose();
        }
        base.Dispose(disposing);
    }
}
