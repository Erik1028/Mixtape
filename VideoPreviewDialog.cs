using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Plays a video stored on the iPod (H.264 .m4v/.mp4) right in the app via a WPF <see cref="MediaEngine"/>.
/// The engine fills the window and the transport docks BELOW it (a sibling, not an overlay — WinForms
/// can't paint on top of the WPF surface). Play/pause + a draggable seek bar with elapsed/total time;
/// Esc or the title-bar × closes.
/// </summary>
internal sealed class VideoPreviewDialog : Form
{
    private readonly MediaEngine _engine = new();
    private readonly TransportBar _bar = new();
    private bool _playing;
    private bool _scrub;
    private double _scrubFrac = -1;

    public VideoPreviewDialog(string filePath, string title)
    {
        Text = string.IsNullOrWhiteSpace(title) ? "Video" : title;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.Black;
        ClientSize = new Size(720, 480 + TransportBar.H);
        KeyPreview = true;

        _engine.Dock = DockStyle.Fill;
        _bar.Dock = DockStyle.Bottom;
        Controls.Add(_engine);
        Controls.Add(_bar);

        _bar.PlayPause += TogglePlay;
        _bar.Seek += frac => { if (_engine.IsOpen) _engine.Position = TimeSpan.FromSeconds(frac * _engine.Duration.TotalSeconds); };
        _bar.Scrub += frac => { _scrub = true; _scrubFrac = frac; _bar.Set(frac, frac * _engine.Duration.TotalSeconds, _engine.Duration.TotalSeconds, _playing); };
        _bar.ScrubEnd += () => _scrub = false;

        _engine.Opened += OnOpened;
        _engine.PositionTick += OnTick;
        _engine.Ended += () => { _playing = false; Refresh(); _bar.Set(1, _engine.Duration.TotalSeconds, _engine.Duration.TotalSeconds, false); };
        _engine.Failed += msg => { if (Application.MessageLoop && Visible) MessageBox.Show(this, msg, "Video", MessageBoxButtons.OK, MessageBoxIcon.Information); BeginInvoke(Close); };

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); else if (e.KeyCode == Keys.Space) TogglePlay(); };

        Shown += (_, _) => { _engine.Load(filePath); _engine.Play(); _playing = true; };
        FormClosed += (_, _) => _engine.CloseMedia();
    }

    private void OnOpened()
    {
        if (_engine.HasVideo)
        {
            var vs = _engine.VideoSize;
            var wa = Screen.FromControl(this).WorkingArea;
            double cap = Math.Min(1.0, Math.Min(wa.Width * 0.8 / vs.Width, (wa.Height * 0.8 - TransportBar.H) / vs.Height));
            int w = Math.Max(360, (int)(vs.Width * Math.Max(cap, 0.5)));
            int h = Math.Max(240, (int)(vs.Height * Math.Max(cap, 0.5)));
            ClientSize = new Size(w, h + TransportBar.H);
            CenterToParent();
        }
        _bar.Set(0, 0, _engine.Duration.TotalSeconds, true);
    }

    private void OnTick()
    {
        if (_scrub) return;
        double dur = _engine.Duration.TotalSeconds, pos = _engine.Position.TotalSeconds;
        _bar.Set(dur > 0 ? pos / dur : 0, pos, dur, _playing);
    }

    private void TogglePlay()
    {
        if (_playing) { _engine.Pause(); _playing = false; } else { _engine.Play(); _playing = true; }
        double dur = _engine.Duration.TotalSeconds, pos = _engine.Position.TotalSeconds;
        _bar.Set(dur > 0 ? pos / dur : 0, pos, dur, _playing);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x00000000; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _engine.Dispose(); _bar.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>The slim owner-painted transport docked under the video.</summary>
    private sealed class TransportBar : Panel
    {
        public const int H = 46;
        public event Action? PlayPause;
        public event Action<double>? Seek;   // commit (mouse up)
        public event Action<double>? Scrub;   // live drag
        public event Action? ScrubEnd;

        private double _frac, _pos, _dur;
        private bool _playing, _hoverPlay, _dragging;

        public TransportBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SidebarBg;
            Height = H;
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            MouseLeave += (_, _) => { if (_hoverPlay) { _hoverPlay = false; Invalidate(); } };
        }

        public void Set(double frac, double pos, double dur, bool playing)
        {
            _frac = Math.Clamp(frac, 0, 1); _pos = pos; _dur = dur; _playing = playing;
            Invalidate();
        }

        private Rectangle PlayRect => new(12, (H - 32) / 2, 32, 32);
        private Rectangle SeekTrack => new(96, H / 2 - 2, Math.Max(40, Width - 96 - 56), 4);

        private void OnDown(object? s, MouseEventArgs e)
        {
            if (PlayRect.Contains(e.Location)) { PlayPause?.Invoke(); return; }
            var t = SeekTrack; var hot = t; hot.Inflate(0, 11);
            if (hot.Contains(e.Location)) { _dragging = true; double f = Math.Clamp((e.X - t.Left) / (double)t.Width, 0, 1); Scrub?.Invoke(f); }
        }
        private void OnMove(object? s, MouseEventArgs e)
        {
            if (_dragging) { var t = SeekTrack; Scrub?.Invoke(Math.Clamp((e.X - t.Left) / (double)t.Width, 0, 1)); return; }
            bool h = PlayRect.Contains(e.Location); if (h != _hoverPlay) { _hoverPlay = h; Invalidate(); }
        }
        private void OnUp(object? s, MouseEventArgs e)
        {
            if (_dragging) { _dragging = false; var t = SeekTrack; Seek?.Invoke(Math.Clamp((e.X - t.Left) / (double)t.Width, 0, 1)); ScrubEnd?.Invoke(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.SidebarBg);
            using (var seam = new Pen(Theme.Border)) g.DrawLine(seam, 0, 0, Width, 0);

            var pr = PlayRect;
            using (var b = new SolidBrush(_hoverPlay ? Theme.AccentBright : Theme.Accent)) g.FillEllipse(b, pr);
            var c = new PointF(pr.X + pr.Width / 2f, pr.Y + pr.Height / 2f);
            using (var p = new SolidBrush(Theme.OnAccent))
            {
                if (_playing) { float bw = 3f, gap = 3f, bh = 11; g.FillRectangle(p, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh); g.FillRectangle(p, c.X + gap / 2, c.Y - bh / 2, bw, bh); }
                else { float ss = 5.5f; g.FillPolygon(p, new[] { new PointF(c.X - ss + 1.2f, c.Y - ss), new PointF(c.X - ss + 1.2f, c.Y + ss), new PointF(c.X + ss + 1.2f, c.Y) }); }
            }

            TextRenderer.DrawText(g, Fmt(_pos), Theme.UiFont(8f), new Rectangle(50, 0, 44, H), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var t = SeekTrack;
            using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1))) { using var tp = Theme.RoundedRect(t, 2); g.FillPath(tb, tp); }
            int fw = (int)Math.Round(t.Width * _frac);
            if (fw > 0) using (var fb = new SolidBrush(Theme.Accent)) { using var fp = Theme.RoundedRect(new Rectangle(t.X, t.Y, fw, t.Height), 2); g.FillPath(fb, fp); }
            using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, t.X + fw - 5, t.Y + t.Height / 2 - 5, 10, 10);
            TextRenderer.DrawText(g, Fmt(_dur), Theme.UiFont(8f), new Rectangle(t.Right + 8, 0, 48, H), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static string Fmt(double sec)
        {
            if (sec < 0 || double.IsNaN(sec)) sec = 0;
            var t = TimeSpan.FromSeconds(sec);
            return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
