using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// iTunes-style MiniPlayer: a small, always-on-top floating window that mirrors and drives the main
/// window's single audio engine (via <see cref="NowPlayingBar"/>'s facade). It shows an ambient
/// blurred-art backdrop, the cover, title, artist, a live seek bar, transport (prev / play-pause /
/// next), volume (slider + scroll) and an "expand back to the full window" button. The whole card is
/// draggable; Esc / double-click-art / closing it all return to the full window (the mini never quits
/// the app). It owns NO playback state — it's a pure view + input: the host pushes state in with
/// <see cref="SetTrack"/> / <see cref="SetProgress"/> and wires its events.
/// </summary>
internal sealed class MiniPlayerForm : Form
{
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? PlayPauseRequested;
    public event Action<double>? SeekRequested;     // 0..1
    public event Action<double>? VolumeRequested;    // 0..1
    public event Action? MuteRequested;
    public event Action? ExpandRequested;            // return to the full window

    private Track? _track;
    private Bitmap? _cover;
    private Bitmap? _backdrop;                // cached blurred ambient background (rebuilt per track)
    private bool _playing;
    private double _dur, _vol = 1;
    private bool _muted;
    private double _posBase;                  // engine position at the last push
    private readonly Stopwatch _sw = new();   // interpolates between ~5 Hz pushes for a smooth seek bar
    private Tween? _anim;                      // ~30 fps repaint while playing (seek + eq bars)
    private double _eqPhase;                   // drives the "now playing" eq bars on the cover
    private double _scrubFrac = -1;           // while dragging the seek bar
    private bool _volDrag;

    private enum Hit { None, Prev, Play, Next, Speaker, Expand }
    private Hit _hover = Hit.None;

    private const int W = 420, H = 166, Pad = 16, ArtSz = 92;

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
        try { if (Environment.ProcessPath is string p) Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); } catch { }

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { if (_hover != Hit.None) { _hover = Hit.None; Invalidate(); } };
        DoubleClick += (_, _) => { if (Layout().Art.Contains(PointToClient(Cursor.Position))) ExpandRequested?.Invoke(); };
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
    public void SetProgress(bool playing, double posSec, double durSec, double volume, bool muted)
    {
        _playing = playing; _dur = durSec; _vol = volume; _muted = muted;
        _posBase = posSec; _sw.Restart();
        if (playing && Visible) StartAnim(); else StopAnim();
        Invalidate();
    }

    private double DisplayPos()
    {
        double p = _posBase + (_playing ? _sw.Elapsed.TotalSeconds : 0);
        return _dur > 0 ? Math.Min(_dur, Math.Max(0, p)) : Math.Max(0, p);
    }

    // A soft blurred wash of the cover behind the controls (Apple-Music "now playing" feel). Cheap blur:
    // downsample the cover hard, then stretch it across the whole card; finish with a dark legibility scrim.
    private void BuildBackdrop()
    {
        _backdrop?.Dispose(); _backdrop = null;
        if (_cover is null) return;
        int w = ClientSize.Width, h = ClientSize.Height;
        var bd = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bd))
        using (var small = new Bitmap(18, 18))
        {
            using (var gs = Graphics.FromImage(small)) { gs.InterpolationMode = InterpolationMode.HighQualityBilinear; gs.PixelOffsetMode = PixelOffsetMode.HighQuality; gs.DrawImage(_cover, new Rectangle(0, 0, 18, 18)); }
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, new Rectangle(-6, -6, w + 12, h + 12)); // overscan so card edges aren't a single sampled row
            using (var scrim = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(150, 12, 12, 14), Color.FromArgb(210, 8, 8, 10), 90f))
                g.FillRectangle(scrim, 0, 0, w, h);
        }
        _backdrop = bd;
    }

    private void StartAnim()
    {
        if (_anim is { IsRunning: true } || !Anim.MotionEnabled) return;
        int tick = 0;
        _anim = Anim.Run(1_000_000_000, _ =>
        {
            _eqPhase += 0.18;
            if ((++tick & 1) == 0 && !IsDisposed) Invalidate();   // ~30 fps (seek + eq both move)
        }, null, Easings.Linear);
    }
    private void StopAnim() { _anim?.Cancel(); _anim = null; }

    // ---- chrome: dark, rounded (DWM), with the standard drop shadow — like the main window ----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }      // dark mode
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { } // rounded corners
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, sizeof(int)); } catch { } // no border line
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _playing) StartAnim(); else if (!Visible) StopAnim();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the mini (Alt+F4, taskbar close, ✕) returns to the full window — it never quits the app.
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

    // ---- layout (one source of truth for paint + hit-testing) ----
    private struct Lo
    {
        public Rectangle Art, Expand, Title, Artist, Seek, LeftTime, RightTime, Prev, Play, Next, Speaker, Vol;
    }

    private Lo Layout()
    {
        int w = ClientSize.Width;
        var l = new Lo
        {
            Art = new Rectangle(Pad, Pad, ArtSz, ArtSz),
            Expand = new Rectangle(w - 14 - 22, 16, 22, 22),
        };
        // title + artist sit in the right zone, vertically centred against the cover
        int tx = l.Art.Right + 16;
        l.Title = new Rectangle(tx, 40, Math.Max(40, l.Expand.Left - 12 - tx), 24);
        l.Artist = new Rectangle(tx, 64, Math.Max(40, w - Pad - tx), 18);

        // seek bar runs full-width BELOW the cover so nothing overlaps the art
        int seekY = 118;
        l.LeftTime = new Rectangle(Pad, seekY - 7, 40, 16);
        l.Seek = new Rectangle(60, seekY, Math.Max(40, w - 120), 4);
        l.RightTime = new Rectangle(w - Pad - 40, seekY - 7, 40, 16);

        // transport centred on the window; volume to the right
        int cx = w / 2, ty = 128;
        l.Play = new Rectangle(cx - 18, ty, 36, 36);
        l.Prev = new Rectangle(l.Play.Left - 12 - 30, ty + 3, 30, 30);
        l.Next = new Rectangle(l.Play.Right + 12, ty + 3, 30, 30);
        l.Vol = new Rectangle(w - Pad - 72, ty + 16, 72, 4);
        l.Speaker = new Rectangle(l.Vol.Left - 8 - 18, ty + 7, 18, 18);
        return l;
    }

    // ---- interaction ----
    private void OnDown(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (e.Button != MouseButtons.Left) return;
        if (l.Expand.Contains(e.Location)) { ExpandRequested?.Invoke(); return; }
        if (l.Speaker.Contains(e.Location)) { MuteRequested?.Invoke(); return; }
        if (Inflate(l.Vol, 4, 9).Contains(e.Location)) { _volDrag = true; SetVolFromX(l.Vol, e.X); return; }
        if (l.Play.Contains(e.Location)) { PlayPauseRequested?.Invoke(); return; }
        if (l.Prev.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (l.Next.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (_track is not null && Inflate(l.Seek, 0, 10).Contains(e.Location)) { _scrubFrac = FracAt(l.Seek, e.X); Invalidate(); return; }
        StartWindowDrag(); // anywhere else: drag the whole window
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (_volDrag) { SetVolFromX(l.Vol, e.X); return; }
        if (_scrubFrac >= 0) { _scrubFrac = FracAt(l.Seek, e.X); Invalidate(); return; }
        var h = l.Expand.Contains(e.Location) ? Hit.Expand
            : l.Speaker.Contains(e.Location) ? Hit.Speaker
            : l.Play.Contains(e.Location) ? Hit.Play
            : l.Prev.Contains(e.Location) ? Hit.Prev
            : l.Next.Contains(e.Location) ? Hit.Next
            : Hit.None;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        if (_scrubFrac >= 0) { SeekRequested?.Invoke(_scrubFrac); _scrubFrac = -1; Invalidate(); }
        _volDrag = false;
    }

    private double FracAt(Rectangle seek, int x) => Math.Clamp((x - seek.Left) / (double)Math.Max(1, seek.Width), 0, 1);
    private void SetVolFromX(Rectangle vol, int x) { double v = FracAt(vol, x); _vol = v; _muted = v <= 0.001; VolumeRequested?.Invoke(v); Invalidate(); }
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

        // ambient backdrop: blurred cover wash, or a themed gradient when there's no art
        if (_backdrop is not null) g.DrawImage(_backdrop, 0, 0, w, h);
        else using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Blend(Theme.SidebarBg, Color.White, 0.05), Theme.Blend(Theme.SidebarBg, Color.Black, 0.18), 90f))
                g.FillRectangle(bg, 0, 0, w, h);
        using (var lip = new Pen(Color.FromArgb(28, 255, 255, 255))) g.DrawLine(lip, 1, 0, w - 1, 0); // crisp top sheen

        var l = Layout();
        bool idle = _track is null;

        // cover (drop shadow + rounded clip; idle = quiet placeholder with a note)
        var cr = l.Art;
        int cvr = (int)Math.Round(cr.Width * Theme.TileFrac);
        var crF = new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1);
        using (var shp = Theme.RoundedRect(new RectangleF(cr.X + 1, cr.Y + 3, cr.Width, cr.Height), cvr))
        using (var sh = new SolidBrush(Color.FromArgb(95, 0, 0, 0))) g.FillPath(sh, shp);
        using (var cp = Theme.RoundedRect(crF, cvr))
        {
            var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (idle)
                using (var ph = new LinearGradientBrush(cr, Theme.Blend(Theme.SidebarBg, Color.White, 0.12), Theme.Blend(Theme.SidebarBg, Color.Black, 0.05), Theme.ArtAngle)) g.FillRectangle(ph, cr);
            else
                g.DrawImage(_cover ?? Theme.MakeArt(cr.Width, (int)(_track!.Dbid & 0xffff)), cr);
            g.Clip = saved;
        }
        if (idle) Theme.DrawNote(g, cr, Color.FromArgb(120, 255, 255, 255));
        using (var bp = new Pen(Color.FromArgb(60, 255, 255, 255))) { using var cp2 = Theme.RoundedRect(crF, cvr); g.DrawPath(bp, cp2); }
        if (!idle && _playing) DrawEqBars(g, cr); // animated "now playing" cue, bottom-right of the cover

        // title + artist/album (white on the dark scrim)
        string title = idle ? "Nothing playing" : _track!.DisplayTitle;
        string sub = idle ? "Pick a song to start"
                          : string.Join("   •   ", new[] { _track!.Artist, _track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        TextRenderer.DrawText(g, title, Theme.UiFont(11.5f, FontStyle.Bold), l.Title, idle ? Color.FromArgb(210, 230, 230, 232) : Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, sub, Theme.UiFont(9f), l.Artist, Color.FromArgb(idle ? 150 : 205, 235, 236, 240),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        DrawExpand(g, l.Expand, _hover == Hit.Expand);

        // seek bar + times
        double pos = _scrubFrac >= 0 ? _scrubFrac * _dur : DisplayPos();
        double frac = _scrubFrac >= 0 ? _scrubFrac : (_dur > 0 ? Math.Clamp(pos / _dur, 0, 1) : 0);
        DrawSlider(g, l.Seek, idle ? 0 : frac, !idle);
        Color tc = Color.FromArgb(190, 225, 226, 230);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(pos), Theme.UiFont(8f), l.LeftTime, tc, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, idle ? "0:00" : Fmt(_dur), Theme.UiFont(8f), l.RightTime, tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // transport
        DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, GlyphPrev, idle);
        DrawPlayButton(g, l.Play, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, l.Next, _hover == Hit.Next, GlyphNext, idle);

        // volume
        DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        DrawSlider(g, l.Vol, _muted ? 0 : _vol, false);
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
        using (var tb = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
        using (var tp = Theme.RoundedRect(t, t.Height / 2f)) g.FillPath(tb, tp);
        float fw = (float)(t.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(Theme.AccentBright))
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            float kx = t.X + fw, ky = t.Y + t.Height / 2f;
            using (var ks = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(ks, kx - 5.5f, ky - 5f, 11, 11);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx - 5, ky - 5, 10, 10);
        }
    }

    /// <summary>Four little accent eq bars bouncing in the cover's bottom-right — the "this is playing" cue.</summary>
    private void DrawEqBars(Graphics g, Rectangle cover)
    {
        const int n = 4, bw = 3, gap = 2, maxH = 14;
        int totalW = n * bw + (n - 1) * gap;
        float baseY = cover.Bottom - 7;
        float x0 = cover.Right - 7 - totalW;
        using (var scrim = new LinearGradientBrush(new RectangleF(cover.Left, cover.Bottom - 22, cover.Width, 22), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(130, 0, 0, 0), 90f))
        {
            var save = g.Clip;
            using (var clip = Theme.RoundedRect(new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 1, cover.Height - 1), cover.Width * Theme.TileFrac))
                g.SetClip(clip, CombineMode.Intersect);
            g.FillRectangle(scrim, cover.Left, cover.Bottom - 22, cover.Width, 22);
            g.Clip = save;
        }
        using var b = new SolidBrush(Theme.AccentBright);
        double[] off = { 0.0, 1.7, 3.3, 5.0 }, spd = { 1.0, 1.35, 0.85, 1.15 };
        for (int i = 0; i < n; i++)
        {
            double v = 0.30 + 0.70 * (0.5 + 0.5 * Math.Sin(_eqPhase * spd[i] + off[i]));
            float bh = (float)(maxH * v);
            g.FillRectangle(b, x0 + i * (bw + gap), baseY - bh, bw, bh);
        }
    }

    private void DrawPlayButton(Graphics g, Rectangle r, bool hover, bool dim)
    {
        // soft halo so the accent disc reads on any backdrop
        using (var halo = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(halo, r.X - 2, r.Y - 1, r.Width + 4, r.Height + 4);
        Color disc = dim ? Color.FromArgb(120, 235, 238, 240) : hover ? Theme.AccentBright : Theme.Accent;
        using (var b = new SolidBrush(disc)) g.FillEllipse(b, r);
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        Color fg = dim ? Color.FromArgb(150, 20, 24, 24) : Theme.OnAccent;
        using var p = new SolidBrush(fg);
        if (_playing && !dim)
        {
            float bw = 3.2f, gap = 3.2f, bh = 11;
            g.FillRectangle(p, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh);
            g.FillRectangle(p, c.X + gap / 2, c.Y - bh / 2, bw, bh);
        }
        else
        {
            float tr = 5.5f;
            g.FillPolygon(p, new[] { new PointF(c.X - tr + 1.5f, c.Y - tr), new PointF(c.X - tr + 1.5f, c.Y + tr), new PointF(c.X + tr + 1.5f, c.Y) });
        }
    }

    private static void DrawCircleGlyph(Graphics g, Rectangle r, bool hover, Action<Graphics, Rectangle, Color> glyph, bool dim)
    {
        if (hover && !dim) { using var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255)); g.FillEllipse(hb, r); }
        glyph(g, r, dim ? Color.FromArgb(120, 235, 238, 240) : hover ? Color.White : Color.FromArgb(225, 240, 241, 244));
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
        Color c = muted ? Color.FromArgb(150, 225, 226, 230) : hover ? Color.White : Color.FromArgb(210, 232, 233, 236);
        using var b = new SolidBrush(c);
        using var p = new Pen(c, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X, cy = r.Y + r.Height / 2f;
        g.FillPolygon(b, new[]
        {
            new PointF(x, cy - 3), new PointF(x + 4, cy - 3), new PointF(x + 9, cy - 6),
            new PointF(x + 9, cy + 6), new PointF(x + 4, cy + 3), new PointF(x, cy + 3),
        });
        if (muted) { g.DrawLine(p, x + 12, cy - 4, x + 17, cy + 4); g.DrawLine(p, x + 17, cy - 4, x + 12, cy + 4); }
        else { g.DrawArc(p, x + 8, cy - 4, 7, 8, -55, 110); g.DrawArc(p, x + 8, cy - 7, 11, 14, -50, 100); }
    }

    /// <summary>"Expand back to the full window" — two diagonal arrows pointing to opposite corners.</summary>
    private static void DrawExpand(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Color.FromArgb(45, 255, 255, 255)); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = hover ? Color.White : Color.FromArgb(210, 232, 233, 236);
        using var p = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.X + 6, y = r.Y + 6, s = r.Width - 12, a = 4.5f; // a = arrow-head leg length
        g.DrawLine(p, x, y, x + s * 0.55f, y + s * 0.55f);
        g.DrawLine(p, x, y, x + a, y);
        g.DrawLine(p, x, y, x, y + a);
        float bx = x + s, by = y + s;
        g.DrawLine(p, bx, by, bx - s * 0.55f, by - s * 0.55f);
        g.DrawLine(p, bx, by, bx - a, by);
        g.DrawLine(p, bx, by, bx, by - a);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim?.Cancel(); _cover?.Dispose(); _backdrop?.Dispose(); }
        base.Dispose(disposing);
    }
}
