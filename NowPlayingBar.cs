using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// An always-visible media transport docked under the content (iTunes/Spotify-style "now playing"):
/// cover, title/artist, prev / play-pause / next, a draggable seek bar with elapsed/total time, an
/// equalizer toggle and a volume slider. It owns a tiny audio-only engine and plays the file straight
/// off the iPod (or a local PC file). When nothing is playing it shows a quiet idle state instead of
/// hiding. Prev/Next are raised so the form can pick the neighbouring track in the current list.
/// </summary>
internal sealed class NowPlayingBar : Panel
{
    private readonly AudioPlayer _engine = new(EqualizerSampleProvider.FlatGains(), false);

    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? EqualizerRequested;

    private Track? _track;
    private Bitmap? _cover;
    private bool _playing;
    private double _volume = 1.0;
    private double _lastVol = 1.0; // last audible level, restored when unmuting from a dragged-to-zero slider
    private bool _muted;
    private bool _eqOn;            // reflected for the EQ icon tint

    private enum Drag { None, Seek, Volume }
    private Drag _drag = Drag.None;
    private double _scrubFrac = -1; // while dragging the seek bar

    public const int H = 88;

    public NowPlayingBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SidebarBg;
        Height = H;

        _engine.Opened += () => Invalidate();
        _engine.PositionTick += () => { if (_playing && _drag != Drag.Seek) Invalidate(); }; // don't repaint when rested/paused
        _engine.Ended += OnEnded;
        _engine.Failed += msg =>
        {
            if (_track is null) return; // a late failure delivered after StopAndHide (e.g. an iPod switch) — ignore
            _playing = false; Invalidate();
            if (!Application.MessageLoop) return;        // never block a headless/automation run
            var form = FindForm();
            if (form is null || !form.Visible) return;    // offscreen render form — don't pop an invisible modal
            BeginInvoke(() => MessageBox.Show(form, msg, "Preview", MessageBoxButtons.OK, MessageBoxIcon.Information));
        };

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { _hover = Hit.None; Invalidate(); };
    }

    public bool IsActive => _track is not null;

    /// <summary>Apply equalizer settings to the audio engine (live + on the next track).</summary>
    public void ApplyEq(bool enabled, float[] gains) { _eqOn = enabled; _engine.SetEqEnabled(enabled); _engine.SetEqGains(gains); Invalidate(); }

    /// <summary>Pause playback (e.g. when a video preview opens) without clearing the bar. Returns true if it was playing.</summary>
    public bool Pause() { if (!_playing) return false; _engine.Pause(); _playing = false; Invalidate(); return true; }

    /// <summary>Resume after an external pause (e.g. when the video preview closes).</summary>
    public void Resume() { if (_track is not null && !_playing) { _engine.Play(); _playing = true; Invalidate(); } }

    /// <summary>Load and play a track's file. <paramref name="cover"/> may be null (a gradient is used).</summary>
    public void Play(Track track, string filePath, Bitmap? cover)
    {
        _track = track;
        _cover?.Dispose();
        _cover = cover is null ? null : new Bitmap(cover);
        _engine.CloseMedia();
        _engine.Volume = _muted ? 0 : _volume;
        _engine.Load(filePath);
        _engine.Play();
        _playing = true;
        _scrubFrac = -1;
        Invalidate();
    }

    /// <summary>Stop playback and return the bar to its idle state (it stays visible).</summary>
    public void StopAndHide()
    {
        _engine.CloseMedia();
        _track = null;
        _cover?.Dispose(); _cover = null;
        _playing = false;
        _scrubFrac = -1;
        Invalidate();
    }

    private void OnEnded()
    {
        // Auto-advance to the next track if the form has one; otherwise rest at the end, paused.
        _playing = false;
        Invalidate();
        NextRequested?.Invoke();
    }

    private void TogglePlay()
    {
        if (_track is null) return;
        if (_playing) { _engine.Pause(); _playing = false; }
        else { _engine.Play(); _playing = true; }
        Invalidate();
    }

    // ---- layout (left = cover+title, centre = controls over a long seek bar, right = EQ + volume) ----
    private const int RightPad = 20, LeftEnd = 268; // LeftEnd = where the cover+title zone ends
    private Rectangle CoverRect => new(18, (H - 56) / 2, 56, 56);

    // right zone, vertically centred
    private Rectangle VolTrack => new(Width - RightPad - 96, H / 2 - 2, 96, 4);
    private Rectangle SpeakerRect => new(VolTrack.Left - 10 - 20, (H - 22) / 2, 20, 22);
    private Rectangle EqRect => new(SpeakerRect.Left - 14 - 24, (H - 24) / 2, 24, 24);
    private int RightStart => EqRect.Left - 16;

    // centre zone spans [LeftEnd, RightStart]; controls sit centred over a long seek bar beneath them
    private int CenterX => (LeftEnd + Math.Max(LeftEnd + 160, RightStart)) / 2;
    private int ControlsY => 13;
    private Rectangle PlayRect => new(CenterX - 19, ControlsY, 38, 38);
    private Rectangle PrevRect => new(PlayRect.Left - 14 - 30, ControlsY + 4, 30, 30);
    private Rectangle NextRect => new(PlayRect.Right + 14, ControlsY + 4, 30, 30);

    private int SeekY => H - 24;
    private Rectangle SeekTrack
    {
        get
        {
            int left = LeftEnd + 44, right = Math.Max(left + 80, RightStart - 44);
            return new Rectangle(left, SeekY, right - left, 5);
        }
    }

    // ---- interaction ----
    private enum Hit { None, Prev, Play, Next, Speaker, Eq }
    private Hit _hover = Hit.None;

    private void OnDown(object? s, MouseEventArgs e)
    {
        // EQ + volume are settings — usable even with nothing loaded.
        if (EqRect.Contains(e.Location)) { EqualizerRequested?.Invoke(); return; }
        if (SpeakerRect.Contains(e.Location))
        {
            _muted = !_muted;
            if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5; // unmuting from a slider-zero → audible again
            _engine.Volume = _muted ? 0 : _volume;
            Invalidate(); return;
        }
        if (Inflate(VolTrack, 0, 9).Contains(e.Location)) { _drag = Drag.Volume; SetVolumeFromX(e.X); return; }

        if (_track is null) return; // transport needs a loaded track
        if (PlayRect.Contains(e.Location)) { TogglePlay(); return; }
        if (PrevRect.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (NextRect.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (Inflate(SeekTrack, 0, 10).Contains(e.Location)) { _drag = Drag.Seek; ScrubTo(e.X); return; }
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        if (_drag == Drag.Volume) { SetVolumeFromX(e.X); return; }
        if (_drag == Drag.Seek) { ScrubTo(e.X); return; }
        var h = SpeakerRect.Contains(e.Location) ? Hit.Speaker
            : EqRect.Contains(e.Location) ? Hit.Eq
            : _track is null ? Hit.None
            : PlayRect.Contains(e.Location) ? Hit.Play
            : PrevRect.Contains(e.Location) ? Hit.Prev
            : NextRect.Contains(e.Location) ? Hit.Next
            : Hit.None;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        if (_drag == Drag.Seek && _scrubFrac >= 0 && _engine.IsOpen)
            _engine.Position = TimeSpan.FromSeconds(_scrubFrac * _engine.Duration.TotalSeconds);
        _scrubFrac = -1;
        _drag = Drag.None;
        Invalidate();
    }

    private void ScrubTo(int x)
    {
        var t = SeekTrack;
        _scrubFrac = Math.Clamp((x - t.Left) / (double)t.Width, 0, 1);
        Invalidate();
    }

    private void SetVolumeFromX(int x)
    {
        var t = VolTrack;
        _volume = Math.Clamp((x - t.Left) / (double)t.Width, 0, 1);
        if (_volume > 0.001) _lastVol = _volume;
        _muted = _volume <= 0.001;
        _engine.Volume = _volume;
        Invalidate();
    }

    private static Rectangle Inflate(Rectangle r, int dx, int dy) { var c = r; c.Inflate(dx, dy); return c; }

    // ---- paint ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // subtle vertical gradient for depth (sits a touch lighter at top, darker at the bottom edge)
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, H),
            Theme.Blend(Theme.SidebarBg, Color.White, 0.02), Theme.Blend(Theme.SidebarBg, Color.Black, 0.10), 90f))
            g.FillRectangle(bg, 0, 0, Width, H);
        using (var seam = new Pen(Theme.Border)) g.DrawLine(seam, 0, 0, Width, 0);

        bool idle = _track is null;
        var cr = CoverRect;

        // cover (soft shadow + rounded, clipped art or an idle placeholder)
        using (var shp = Theme.RoundedRect(new RectangleF(cr.X + 1, cr.Y + 2, cr.Width, cr.Height), 10))
        using (var sh = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillPath(sh, shp);
        using (var cp = Theme.RoundedRect(cr, 10))
        {
            var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (idle)
                using (var ph = new LinearGradientBrush(cr, Color.FromArgb(46, 52, 64), Color.FromArgb(28, 32, 40), 45f)) g.FillRectangle(ph, cr);
            else
                // MakeArt returns a cache-owned bitmap — never dispose it; _cover is the bar's own clone.
                g.DrawImage(_cover ?? Theme.MakeArt(cr.Width, (int)(_track!.Dbid & 0xffff)), cr);
            g.Clip = saved;
        }
        if (idle)
            TextRenderer.DrawText(g, "♪", Theme.UiFont(20f), cr, Color.FromArgb(95, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using (var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.10))) { using var cp2 = Theme.RoundedRect(cr, 10); g.DrawPath(bp, cp2); }

        // title / artist (vertically centred in the left zone)
        int tx = cr.Right + 14, tw = LeftEnd - tx - 8;
        string title = idle ? "Nothing playing" : _track!.DisplayTitle;
        string sub = idle ? "Pick a song to start"
                          : string.Join("  •  ", new[] { _track!.Artist, _track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        TextRenderer.DrawText(g, title, Theme.UiFont(10.5f, FontStyle.Bold),
            new Rectangle(tx, H / 2 - 20, tw, 20), idle ? Theme.Subtle : Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, sub, Theme.UiFont(8.75f),
            new Rectangle(tx, H / 2 + 2, tw, 18), idle ? Theme.Faint : Theme.Subtle,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // transport (dimmed + inert when idle)
        DrawCircleGlyph(g, PrevRect, _hover == Hit.Prev, GlyphPrev, idle);
        DrawPlayButton(g, PlayRect, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, NextRect, _hover == Hit.Next, GlyphNext, idle);

        // seek
        double dur = _engine.Duration.TotalSeconds;
        double pos = _engine.IsOpen ? _engine.Position.TotalSeconds : 0;
        double frac = _scrubFrac >= 0 ? _scrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
        var st = SeekTrack;
        DrawSlider(g, st, idle ? 0 : frac, !idle);
        double shown = _scrubFrac >= 0 ? _scrubFrac * dur : pos;
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(shown), Theme.UiFont(8f), new Rectangle(st.Left - 46, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(dur), Theme.UiFont(8f), new Rectangle(st.Right + 6, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // equalizer + volume (always interactive)
        DrawEqGlyph(g, EqRect, _hover == Hit.Eq);
        DrawSpeaker(g, SpeakerRect, _muted, _hover == Hit.Speaker);
        DrawSlider(g, VolTrack, _muted ? 0 : _volume, false);
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static void DrawSlider(Graphics g, Rectangle track, double frac, bool knob)
    {
        using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1)))
        using (var tp = Theme.RoundedRect(track, track.Height / 2f)) g.FillPath(tb, tp);
        int fw = (int)Math.Round(track.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(Theme.Accent))
            using (var fp = Theme.RoundedRect(new Rectangle(track.X, track.Y, fw, track.Height), track.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            int kx = track.X + fw, ky = track.Y + track.Height / 2;
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx - 5, ky - 5, 10, 10);
        }
    }

    private void DrawPlayButton(Graphics g, Rectangle r, bool hover, bool dim)
    {
        Color disc = dim ? Theme.Blend(Theme.SidebarBg, Color.White, 0.12)
                         : hover ? Theme.AccentBright : Theme.Accent;
        using (var b = new SolidBrush(disc)) g.FillEllipse(b, r);
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        Color fg = dim ? Theme.Faint : Theme.OnAccent;
        if (_playing && !dim)
        {
            float bw = 3.5f, gap = 3.5f, bh = 13;
            using var p = new SolidBrush(fg);
            g.FillRectangle(p, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh);
            g.FillRectangle(p, c.X + gap / 2, c.Y - bh / 2, bw, bh);
        }
        else
        {
            using var p = new SolidBrush(fg);
            float s = 6.5f;
            g.FillPolygon(p, new[] { new PointF(c.X - s + 1.5f, c.Y - s), new PointF(c.X - s + 1.5f, c.Y + s), new PointF(c.X + s + 1.5f, c.Y) });
        }
    }

    private static void DrawCircleGlyph(Graphics g, Rectangle r, bool hover, Action<Graphics, Rectangle, Color> glyph, bool dim)
    {
        if (hover && !dim) { using var hb = new SolidBrush(Theme.RowHover); g.FillEllipse(hb, r); }
        glyph(g, r, dim ? Theme.Faint : hover ? Theme.TextCol : Theme.Subtle);
    }

    private static void GlyphPrev(Graphics g, Rectangle r, Color c)
    {
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var b = new SolidBrush(c);
        float s = 5f;
        g.FillPolygon(b, new[] { new PointF(m.X + 1, m.Y - s), new PointF(m.X + 1, m.Y + s), new PointF(m.X - s + 1, m.Y) });
        g.FillRectangle(b, m.X - s - 1.5f, m.Y - s, 2.2f, s * 2);
    }

    private static void GlyphNext(Graphics g, Rectangle r, Color c)
    {
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var b = new SolidBrush(c);
        float s = 5f;
        g.FillPolygon(b, new[] { new PointF(m.X - 1, m.Y - s), new PointF(m.X - 1, m.Y + s), new PointF(m.X + s - 1, m.Y) });
        g.FillRectangle(b, m.X + s - 0.7f, m.Y - s, 2.2f, s * 2);
    }

    private static void DrawSpeaker(Graphics g, Rectangle r, bool muted, bool hover)
    {
        Color c = muted ? Theme.Faint : hover ? Theme.TextCol : Theme.Subtle;
        using var b = new SolidBrush(c);
        using var p = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X, cy = r.Y + r.Height / 2f;
        // speaker body
        g.FillPolygon(b, new[]
        {
            new PointF(x, cy - 3), new PointF(x + 5, cy - 3), new PointF(x + 10, cy - 7),
            new PointF(x + 10, cy + 7), new PointF(x + 5, cy + 3), new PointF(x, cy + 3),
        });
        if (muted)
        {
            g.DrawLine(p, x + 13, cy - 5, x + 19, cy + 5);
            g.DrawLine(p, x + 19, cy - 5, x + 13, cy + 5);
        }
        else
        {
            g.DrawArc(p, x + 9, cy - 5, 8, 10, -55, 110);
            g.DrawArc(p, x + 9, cy - 8, 12, 16, -50, 100);
        }
    }

    private void DrawEqGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, 6); g.FillPath(hb, hp); }
        // three little sliders (EQ icon); tinted accent when the EQ is on
        Color c = _eqOn ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
        using var bar = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(c);
        float[] xs = { r.X + 7, r.X + 12, r.X + 17 };
        float top = r.Y + 6, bot = r.Bottom - 6;
        float[] knob = { r.Y + 13, r.Y + 9, r.Y + 15 }; // each slider's knob height
        for (int i = 0; i < 3; i++)
        {
            g.DrawLine(bar, xs[i], top, xs[i], bot);
            g.FillEllipse(dot, xs[i] - 2.6f, knob[i] - 2.6f, 5.2f, 5.2f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _engine.Dispose(); _cover?.Dispose(); }
        base.Dispose(disposing);
    }
}
