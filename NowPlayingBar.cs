using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// An always-visible media transport docked under the content (iTunes/Spotify-style "now playing"):
/// cover, title/artist, prev / play-pause / next, a draggable seek bar with elapsed/total time, an
/// equalizer toggle and a volume slider. It owns a tiny audio-only engine and plays the file straight
/// off the iPod (or a local PC file). When nothing is playing it shows a quiet idle state instead of
/// hiding. The layout is RESPONSIVE — as the window narrows it drops the volume slider, then the EQ,
/// then the seek times, then the seek bar, then the title — so the controls never overlap.
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
    private const int RightPad = 20, ControlsY = 13;
    private int SeekY => H - 24;

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

    // ---- responsive layout (one source of truth for paint + hit-testing) ----
    private struct Lo
    {
        public Rectangle Cover; public int TextX, TextW; public bool ShowTitle;
        public Rectangle Prev, Play, Next;
        public Rectangle Seek; public bool ShowSeek, ShowTimes;
        public Rectangle Eq; public bool ShowEq;
        public Rectangle Speaker; public bool ShowSpeaker;
        public Rectangle Vol; public bool ShowVol;
    }

    private Lo Layout()
    {
        int w = Width;
        var l = new Lo { Cover = new Rectangle(16, (H - 56) / 2, 56, 56) };
        int leftBound = l.Cover.Right + 8;

        // Right cluster, built from the right edge inward; widgets appear only when there's room.
        l.ShowVol = w >= 660;
        l.ShowSpeaker = w >= 470;
        l.ShowEq = w >= 540;
        int rc = w - RightPad;
        if (l.ShowVol) { l.Vol = new Rectangle(rc - 92, H / 2 - 2, 92, 4); rc = l.Vol.Left - 12; }
        if (l.ShowSpeaker) { l.Speaker = new Rectangle(rc - 20, (H - 22) / 2, 20, 22); rc = l.Speaker.Left - 14; }
        if (l.ShowEq) { l.Eq = new Rectangle(rc - 24, (H - 24) / 2, 24, 24); rc = l.Eq.Left - 14; }
        int rightStart = rc;

        // Transport (122px block) centred between the cover and the right cluster, clamped so it never overlaps either.
        const int half = 61;
        int cx = Math.Clamp((leftBound + rightStart) / 2, leftBound + half, Math.Max(leftBound + half, rightStart - half));
        l.Play = new Rectangle(cx - 19, ControlsY, 38, 38);
        l.Prev = new Rectangle(l.Play.Left - 12 - 30, ControlsY + 4, 30, 30);
        l.Next = new Rectangle(l.Play.Right + 12, ControlsY + 4, 30, 30);

        // Title zone, left of the transport — hidden when there isn't enough room.
        l.TextX = l.Cover.Right + 12;
        l.TextW = l.Prev.Left - 12 - l.TextX;
        l.ShowTitle = l.TextW >= 90;

        // Seek bar at the bottom, spanning the centre; times at the ends only when wide.
        l.ShowSeek = w >= 460 && rightStart - leftBound >= 150;
        l.ShowTimes = w >= 740;
        int sL = leftBound + (l.ShowTimes ? 44 : 4);
        int sR = rightStart - (l.ShowTimes ? 44 : 4);
        l.Seek = new Rectangle(sL, SeekY, Math.Max(60, sR - sL), 5);
        return l;
    }

    // ---- interaction ----
    private enum Hit { None, Prev, Play, Next, Speaker, Eq }
    private Hit _hover = Hit.None;

    private void OnDown(object? s, MouseEventArgs e)
    {
        var l = Layout();
        // EQ + volume are settings — usable even with nothing loaded.
        if (l.ShowEq && l.Eq.Contains(e.Location)) { EqualizerRequested?.Invoke(); return; }
        if (l.ShowSpeaker && l.Speaker.Contains(e.Location))
        {
            _muted = !_muted;
            if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5;
            _engine.Volume = _muted ? 0 : _volume;
            Invalidate(); return;
        }
        if (l.ShowVol && Inflate(l.Vol, 0, 9).Contains(e.Location)) { _drag = Drag.Volume; SetVolumeFromX(l.Vol, e.X); return; }

        if (_track is null) return; // transport needs a loaded track
        if (l.Play.Contains(e.Location)) { TogglePlay(); return; }
        if (l.Prev.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (l.Next.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (l.ShowSeek && Inflate(l.Seek, 0, 10).Contains(e.Location)) { _drag = Drag.Seek; ScrubTo(l.Seek, e.X); return; }
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (_drag == Drag.Volume && l.ShowVol) { SetVolumeFromX(l.Vol, e.X); return; }
        if (_drag == Drag.Seek && l.ShowSeek) { ScrubTo(l.Seek, e.X); return; }
        var h = l.ShowSpeaker && l.Speaker.Contains(e.Location) ? Hit.Speaker
            : l.ShowEq && l.Eq.Contains(e.Location) ? Hit.Eq
            : _track is null ? Hit.None
            : l.Play.Contains(e.Location) ? Hit.Play
            : l.Prev.Contains(e.Location) ? Hit.Prev
            : l.Next.Contains(e.Location) ? Hit.Next
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

    private void ScrubTo(Rectangle seek, int x)
    {
        _scrubFrac = Math.Clamp((x - seek.Left) / (double)seek.Width, 0, 1);
        Invalidate();
    }

    private void SetVolumeFromX(Rectangle vol, int x)
    {
        _volume = Math.Clamp((x - vol.Left) / (double)vol.Width, 0, 1);
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
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, H),
            Theme.Blend(Theme.SidebarBg, Color.White, 0.03), Theme.Blend(Theme.SidebarBg, Color.Black, 0.12), 90f))
        {
            bg.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Theme.Blend(Theme.SidebarBg, Color.White, 0.03), Theme.SidebarBg, Theme.Blend(Theme.SidebarBg, Color.Black, 0.12) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            g.FillRectangle(bg, 0, 0, Width, H);
        }
        using (var seam = new Pen(Theme.Border)) g.DrawLine(seam, 0, 0, Width, 0);

        var l = Layout();
        bool idle = _track is null;
        var cr = l.Cover;
        int cvr = (int)Math.Round(cr.Width * Theme.TileFrac);

        // cover (soft shadow + rounded, clipped art or an idle placeholder)
        using (var shp = Theme.RoundedRect(new RectangleF(cr.X + 1, cr.Y + 2, cr.Width, cr.Height), cvr))
        using (var sh = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillPath(sh, shp);
        using (var cp = Theme.RoundedRect(cr, cvr))
        {
            var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (idle)
                // a quiet recessed tile (just above the bar's own shade), not a bright grey box
                using (var ph = new LinearGradientBrush(cr, Theme.Blend(Theme.SidebarBg, Color.White, 0.09), Theme.Blend(Theme.SidebarBg, Color.Black, 0.06), Theme.ArtAngle)) g.FillRectangle(ph, cr);
            else
                g.DrawImage(_cover ?? Theme.MakeArt(cr.Width, (int)(_track!.Dbid & 0xffff)), cr);
            g.Clip = saved;
        }
        if (idle)
            TextRenderer.DrawText(g, "♪", Theme.UiFont(20f), cr, Color.FromArgb(95, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using (var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.10))) { using var cp2 = Theme.RoundedRect(cr, cvr); g.DrawPath(bp, cp2); }

        // title / artist (hidden when the window is too narrow)
        if (l.ShowTitle)
        {
            string title = idle ? "Nothing playing" : _track!.DisplayTitle;
            string sub = idle ? "Pick a song to start"
                              : string.Join("  •  ", new[] { _track!.Artist, _track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
            TextRenderer.DrawText(g, title, Theme.UiFont(10.5f, FontStyle.Bold),
                new Rectangle(l.TextX, H / 2 - 20, l.TextW, 20), idle ? Theme.Subtle : Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sub, Theme.UiFont(8.75f),
                new Rectangle(l.TextX, H / 2 + 2, l.TextW, 18), idle ? Theme.Faint : Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // transport (dimmed + inert when idle)
        DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, GlyphPrev, idle);
        DrawPlayButton(g, l.Play, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, l.Next, _hover == Hit.Next, GlyphNext, idle);

        // seek
        if (l.ShowSeek)
        {
            double dur = _engine.Duration.TotalSeconds;
            double pos = _engine.IsOpen ? _engine.Position.TotalSeconds : 0;
            double frac = _scrubFrac >= 0 ? _scrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
            DrawSlider(g, l.Seek, idle ? 0 : frac, !idle);
            if (l.ShowTimes)
            {
                double shown = _scrubFrac >= 0 ? _scrubFrac * dur : pos;
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(shown), Theme.UiFont(8f), new Rectangle(l.Seek.Left - 46, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(dur), Theme.UiFont(8f), new Rectangle(l.Seek.Right + 6, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        // equalizer + volume (always interactive when shown)
        if (l.ShowEq) DrawEqGlyph(g, l.Eq, _hover == Hit.Eq);
        if (l.ShowSpeaker) DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        if (l.ShowVol) DrawSlider(g, l.Vol, _muted ? 0 : _volume, false);
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static void DrawSlider(Graphics g, Rectangle track, double frac, bool knob)
    {
        var t = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1);
        using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1)))
        using (var tp = Theme.RoundedRect(t, t.Height / 2f)) g.FillPath(tb, tp);
        float fw = (float)(t.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(Theme.Accent))
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            float kx = t.X + fw, ky = t.Y + t.Height / 2f;
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
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = _eqOn ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
        using var bar = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(c);
        float[] xs = { r.X + 7, r.X + 12, r.X + 17 };
        float top = r.Y + 6, bot = r.Bottom - 6;
        float[] knob = { r.Y + 13, r.Y + 9, r.Y + 15 };
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
