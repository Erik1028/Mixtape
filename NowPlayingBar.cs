using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A slim media transport docked under the content (iTunes-style "now playing"): cover, title/artist,
/// prev / play-pause / next, a draggable seek bar with elapsed/total time, a volume slider and a close
/// button. It owns a tiny audio-only <see cref="MediaEngine"/> and plays the file straight off the iPod.
/// Hidden (row collapsed) until something is played. Prev/Next are raised so the form can pick the
/// neighbouring track in the current list.
/// </summary>
internal sealed class NowPlayingBar : Panel
{
    private readonly AudioPlayer _engine = new(EqualizerSampleProvider.FlatGains(), false);

    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? CloseRequested;
    public event Action? EqualizerRequested;

    private Track? _track;
    private Bitmap? _cover;
    private bool _playing;
    private double _volume = 1.0;
    private double _lastVol = 1.0; // last audible level, restored when unmuting from a dragged-to-zero slider
    private bool _muted;

    private enum Drag { None, Seek, Volume }
    private Drag _drag = Drag.None;
    private double _scrubFrac = -1; // while dragging the seek bar

    public const int H = 84;

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

    /// <summary>Pause playback (e.g. when a video preview opens) without closing the bar. Returns true if it was playing.</summary>
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

    public void StopAndHide()
    {
        _engine.CloseMedia();
        _track = null;
        _cover?.Dispose(); _cover = null;
        _playing = false;
        CloseRequested?.Invoke();
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

    // ---- layout (Spotify-style: left = cover+title, centre = controls over a long seek bar, right = volume) ----
    private const int RightPad = 16, LeftEnd = 240; // LeftEnd = where the cover+title zone ends
    private Rectangle CoverRect => new(16, (H - 48) / 2, 48, 48);

    // right zone, vertically centred
    private Rectangle CloseRect => new(Width - RightPad - 24, (H - 24) / 2, 24, 24);
    private Rectangle VolTrack => new(CloseRect.Left - 16 - 92, H / 2 - 2, 92, 4);
    private Rectangle SpeakerRect => new(VolTrack.Left - 8 - 20, (H - 22) / 2, 20, 22);
    private Rectangle EqRect => new(SpeakerRect.Left - 12 - 24, (H - 24) / 2, 24, 24);
    private int RightStart => EqRect.Left - 14;

    // centre zone spans [LeftEnd, RightStart]; controls sit centred over a long seek bar beneath them
    private int CenterX => (LeftEnd + Math.Max(LeftEnd + 160, RightStart)) / 2;
    private int ControlsY => 11;
    private Rectangle PlayRect => new(CenterX - 19, ControlsY, 38, 38);
    private Rectangle PrevRect => new(PlayRect.Left - 12 - 30, ControlsY + 4, 30, 30);
    private Rectangle NextRect => new(PlayRect.Right + 12, ControlsY + 4, 30, 30);

    private int SeekY => H - 22;
    private Rectangle SeekTrack
    {
        get
        {
            int left = LeftEnd + 44, right = Math.Max(left + 80, RightStart - 44);
            return new Rectangle(left, SeekY, right - left, 5);
        }
    }

    // ---- interaction ----
    private enum Hit { None, Prev, Play, Next, Close, Speaker, Eq }
    private Hit _hover = Hit.None;

    private void OnDown(object? s, MouseEventArgs e)
    {
        if (_track is null) return;
        if (PlayRect.Contains(e.Location)) { TogglePlay(); return; }
        if (PrevRect.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (NextRect.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (CloseRect.Contains(e.Location)) { StopAndHide(); return; }
        if (EqRect.Contains(e.Location)) { EqualizerRequested?.Invoke(); return; }
        if (SpeakerRect.Contains(e.Location))
        {
            _muted = !_muted;
            if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5; // unmuting from a slider-zero → audible again
            _engine.Volume = _muted ? 0 : _volume;
            Invalidate(); return;
        }
        if (Inflate(VolTrack, 0, 9).Contains(e.Location)) { _drag = Drag.Volume; SetVolumeFromX(e.X); return; }
        if (Inflate(SeekTrack, 0, 10).Contains(e.Location)) { _drag = Drag.Seek; ScrubTo(e.X); return; }
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        if (_drag == Drag.Volume) { SetVolumeFromX(e.X); return; }
        if (_drag == Drag.Seek) { ScrubTo(e.X); return; }
        var h = _track is null ? Hit.None
            : PlayRect.Contains(e.Location) ? Hit.Play
            : PrevRect.Contains(e.Location) ? Hit.Prev
            : NextRect.Contains(e.Location) ? Hit.Next
            : CloseRect.Contains(e.Location) ? Hit.Close
            : SpeakerRect.Contains(e.Location) ? Hit.Speaker
            : EqRect.Contains(e.Location) ? Hit.Eq
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
        g.Clear(Theme.SidebarBg);
        using (var seam = new Pen(Theme.Border)) g.DrawLine(seam, 0, 0, Width, 0);
        if (_track is null) return;

        // cover
        var cr = CoverRect;
        using (var cp = Theme.RoundedRect(cr, 8))
        {
            var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // MakeArt returns a cache-owned bitmap — never dispose it; _cover is the bar's own clone
            // and is drawn (not freed) here. (A `using` on the MakeArt fallback freed the cached
            // gradient and crashed the next repaint with "Parameter is not valid".)
            g.DrawImage(_cover ?? Theme.MakeArt(cr.Width, (int)(_track.Dbid & 0xffff)), cr);
            g.Clip = saved;
        }
        using (var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.08))) { using var cp2 = Theme.RoundedRect(cr, 8); g.DrawPath(bp, cp2); }

        // title / artist (vertically centred in the left zone)
        int tx = cr.Right + 12, tw = LeftEnd - tx - 8;
        TextRenderer.DrawText(g, _track.DisplayTitle, Theme.UiFont(10f, FontStyle.Bold),
            new Rectangle(tx, H / 2 - 19, tw, 20), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        string sub = string.Join("  •  ", new[] { _track.Artist, _track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        TextRenderer.DrawText(g, sub, Theme.UiFont(8.75f),
            new Rectangle(tx, H / 2 + 1, tw, 18), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // transport
        DrawCircleGlyph(g, PrevRect, _hover == Hit.Prev, GlyphPrev);
        DrawPlayButton(g, PlayRect, _hover == Hit.Play);
        DrawCircleGlyph(g, NextRect, _hover == Hit.Next, GlyphNext);

        // seek
        double dur = _engine.Duration.TotalSeconds;
        double pos = _engine.IsOpen ? _engine.Position.TotalSeconds : 0;
        double frac = _scrubFrac >= 0 ? _scrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
        var st = SeekTrack;
        DrawSlider(g, st, frac, true);
        double shown = _scrubFrac >= 0 ? _scrubFrac * dur : pos;
        TextRenderer.DrawText(g, Fmt(shown), Theme.UiFont(8f), new Rectangle(st.Left - 46, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Fmt(dur), Theme.UiFont(8f), new Rectangle(st.Right + 6, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // equalizer
        DrawEqGlyph(g, EqRect, _hover == Hit.Eq);

        // volume
        DrawSpeaker(g, SpeakerRect, _muted, _hover == Hit.Speaker);
        DrawSlider(g, VolTrack, _muted ? 0 : _volume, false);

        // close
        DrawClose(g, CloseRect, _hover == Hit.Close);
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

    private void DrawPlayButton(Graphics g, Rectangle r, bool hover)
    {
        using (var b = new SolidBrush(hover ? Theme.AccentBright : Theme.Accent)) g.FillEllipse(b, r);
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        if (_playing)
        {
            float bw = 3.5f, gap = 3.5f, bh = 13;
            using var p = new SolidBrush(Theme.OnAccent);
            g.FillRectangle(p, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh);
            g.FillRectangle(p, c.X + gap / 2, c.Y - bh / 2, bw, bh);
        }
        else
        {
            using var p = new SolidBrush(Theme.OnAccent);
            float s = 6.5f;
            g.FillPolygon(p, new[] { new PointF(c.X - s + 1.5f, c.Y - s), new PointF(c.X - s + 1.5f, c.Y + s), new PointF(c.X + s + 1.5f, c.Y) });
        }
    }

    private static void DrawCircleGlyph(Graphics g, Rectangle r, bool hover, Action<Graphics, Rectangle, Color> glyph)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); g.FillEllipse(hb, r); }
        glyph(g, r, hover ? Theme.TextCol : Theme.Subtle);
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
        bool on = _eqOn;
        Color c = on ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
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

    private bool _eqOn; // reflected for the EQ icon tint

    private static void DrawClose(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, r.Width / 2f); g.FillPath(hb, hp); }
        using var p = new Pen(hover ? Theme.TextCol : Theme.Faint, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int m = 8;
        g.DrawLine(p, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
        g.DrawLine(p, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _engine.Dispose(); _cover?.Dispose(); }
        base.Dispose(disposing);
    }
}
