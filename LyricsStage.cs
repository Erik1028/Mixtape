using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The full-window lyrics view — the Apple Music arrangement: the cover with its own transport on the left,
/// the words large on the right, everything sitting on a slow, blurred wash of the album art's colours.
/// It takes the WHOLE window (sidebar, list and bar disappear under it), so it carries what the bar would
/// otherwise give: play/pause, skip, seek and volume — and the window can still be dragged by its top edge.
///
/// It is fed by the host: the track and cover, the resolved lyric lines, and the player's clock a few times
/// a second (interpolated here). Nothing in it fetches or blocks.
///
/// Text is drawn with GDI+ rather than TextRenderer on purpose. On a colourful blurred ground the words must
/// be TRANSLUCENT white — that is the whole look — and only GDI+ can draw text with an alpha (and only GDI+
/// honours the clip). Grayscale antialiasing suits a coloured ground better than ClearType anyway.
/// </summary>
internal sealed class LyricsStage : Control
{
    public event Action? CloseRequested;
    public event Action? PlayPauseRequested;
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action<double>? SeekFractionRequested;
    public event Action<TimeSpan>? SeekToRequested;     // a lyric line was clicked
    public event Action<double>? VolumeRequested;        // 0..1
    public event Action? MoreRequested;                  // the "···": sync nudge, other lyrics — the popover
    /// <summary>The stage is the CONTENT CARD under the top deck: the deck keeps the transport, the rail stays.
    /// No second transport, seek or volume here; "···" sits in the corner; the card's corners are carved.</summary>
    public bool UnderDeck { get; set; }

    // ---- what the host gives us --------------------------------------------
    private string _title = "", _artist = "", _format = "";   // _format: "FLAC · 1 016 kbps · 44,1 kHz", a quiet chip under the artist
    private Bitmap? _cover, _coverScaled;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private bool _synced;
    private string _status = "";
    private TimeSpan _reported, _duration, _offset;
    private readonly Stopwatch _since = new();
    private bool _playing;
    private double _volume = 1;

    // ---- the wash ------------------------------------------------------------
    // Two soft colour fields made from the cover (one of them turned round), drawn scaled up and drifting on
    // incommensurate periods so the picture never repeats. Small bitmaps, scaled on the way out: cheap.
    private Bitmap? _washA, _washB;
    // A track change cross-dissolves: the old wash and cover stay for a moment under the new ones.
    private Bitmap? _oldWashA, _oldWashB, _oldCoverScaled;
    private double _swapT0;
    private const int SwapMs = 650;
    /// <summary>How much of the cover's colour the card takes: Poster = the full-strength field (the original);
    /// Tint = the app's own dark card with the cover's colour breathing through; Glow = the colour lives behind the
    /// cover and dies out toward the words, which sit on the plain card surface.</summary>
    internal enum WashStyle { Poster, Tint, Glow }
    internal static WashStyle Style = WashStyle.Tint;   // Erik's pick (2026-09-13): the app's own dark card with the cover breathing through
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private FrameClock? _clockPump;                 // display-paced repaints; see FrameClock

    // ---- the sheet -------------------------------------------------------------
    private sealed class Word { public string W = ""; public int VisualLine; public float Wd; }
    private sealed class Row { public int Top, Height, LineH; public List<Word> Words = new(); public List<string> Lines = new(); public bool Beat; }
    private Row[] _rows = Array.Empty<Row>();
    private int _measuredFor = -1;
    private double _scroll, _userScroll;
    private double _lastUserScrollTick;
    private bool _landed;
    private int _shownCur = -1, _prevCur = -1;
    // Milliseconds off _clock, not Environment.TickCount. TickCount moves in 15.6 ms steps, so every
    // one of these phases advanced in jumps -- invisible at 40 fps because the frames were 27 ms apart
    // anyway, and the first thing you would see once the frames got shorter than the clock.
    private double _handoffT0, _revealT0;
    private readonly Stopwatch _frameTime = Stopwatch.StartNew();
    private int _hover = -1;
    private const double BeatSeconds = 4.0;
    private const int HandoffMs = 320;
    private const float Pop = 1.06f;                    // the sung line grows by this much
    private static readonly TimeSpan MaxDrift = TimeSpan.FromSeconds(2);

    // The words can be put away to leave the cover alone in the middle — Apple's speech-bubble button.
    private double _sheetShown = 1;
    private Tween? _sheetTween;

    // ---- chrome ----------------------------------------------------------------
    private enum Hit { None, Prev, Play, Next, Seek, Volume, Bubble, More, Close }
    private Hit _hot = Hit.None;
    private bool _seeking, _volDrag;
    private float _intro = 1f;
    private Tween? _introTween;

    private float _lineSize;
    private Font? _fLine;
    private readonly Dictionary<int, Font> _fontCache = new();   // by size×10, for the sung line's growth
    private readonly Font _fTime = Theme.UiFont(8.5f);
    private readonly Font _fSub = Theme.UiFont(10.5f);
    private readonly Font _fTitle = Theme.DisplayFont(13f, FontStyle.Bold);
    private static readonly StringFormat Typo = (StringFormat)StringFormat.GenericTypographic.Clone();

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public LyricsStage()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        TabStop = true;
        Typo.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        // Paced by the compositor, not by a WinForms timer. The timer that used to be here asked for
        // 16 ms and delivered a repaint every 27 ms on average, with two thirds of frames landing over
        // 20 ms -- 40 fps, visibly stepping, on a 180 Hz panel. Raising the multimedia timer period did
        // nothing for it; measured, with and without, at the same 27 ms. FrameClock waits on the vertical
        // blank instead and lands every frame inside 7.6 ms at the 99th percentile.
        _clockPump = new FrameClock(this);
        VisibleChanged += (_, _) =>
        {
            if (Visible) _clockPump?.Start();
            else _clockPump?.Stop();
        };

        MouseMove += (_, e) =>
        {
            if (_seeking) { SeekFractionRequested?.Invoke(SeekFrac(e.X)); Invalidate(); return; }
            if (_volDrag) { _volume = VolFrac(e.X); VolumeRequested?.Invoke(_volume); Invalidate(); return; }
            var h = HitAt(e.Location);
            int hv = _synced && h == Hit.None ? LineAt(e.Location) : -1;
            if (h != _hot || hv != _hover) { _hot = h; _hover = hv; Cursor = h != Hit.None || hv >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        };
        MouseLeave += (_, _) => { _hot = Hit.None; _hover = -1; Cursor = Cursors.Default; Invalidate(); };
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var h = HitAt(e.Location);
            if (h == Hit.Seek) { _seeking = true; SeekFractionRequested?.Invoke(SeekFrac(e.X)); return; }
            if (h == Hit.Volume) { _volDrag = true; _volume = VolFrac(e.X); VolumeRequested?.Invoke(_volume); Invalidate(); return; }
            // The window's own caption is under us (full stage only), so the empty top band drags the window, as a title bar would.
            if (!UnderDeck && h == Hit.None && e.Y < 48 && LineAt(e.Location) < 0)
            {
                var form = FindForm();
                if (form is not null) { try { ReleaseCapture(); SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } }
            }
        };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { _seeking = false; _volDrag = false; } };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitAt(e.Location))
            {
                case Hit.Prev: PrevRequested?.Invoke(); return;
                case Hit.Play: PlayPauseRequested?.Invoke(); return;
                case Hit.Next: NextRequested?.Invoke(); return;
                case Hit.Close: CloseRequested?.Invoke(); return;
                case Hit.Bubble: ToggleSheet(); return;
                case Hit.More: MoreRequested?.Invoke(); return;
                case Hit.Seek: case Hit.Volume: return;
            }
            int i = _synced ? LineAt(e.Location) : -1;
            if (i >= 0 && i < _lines.Count)
            {
                if (_playing) { _reported = _lines[i].At + _offset; _since.Restart(); }   // answer the hand now
                SeekToRequested?.Invoke(_lines[i].At + _offset);
            }
        };
        MouseWheel += (_, e) =>
        {
            if (_lines.Count == 0 || _sheetShown < 0.5) return;
            // Timed: a peek away from the followed line (it eases back). Untimed: this IS the scroll, so it
            // stays put and is clamped to the sheet.
            _userScroll = _synced
                ? Math.Clamp(_userScroll - e.Delta * 0.8, -ContentHeight, ContentHeight)
                : Math.Clamp(_userScroll - e.Delta * 0.8, 0, Math.Max(0, ContentHeight - Height + 40));
            _lastUserScrollTick = _clock.Elapsed.TotalMilliseconds;
            Invalidate();
        };
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Escape or Keys.Space or Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape: CloseRequested?.Invoke(); break;
            case Keys.Space: PlayPauseRequested?.Invoke(); break;
            case Keys.Right: if (_duration > TimeSpan.Zero) SeekFractionRequested?.Invoke(Math.Clamp((Now + _offset + TimeSpan.FromSeconds(5)).TotalSeconds / _duration.TotalSeconds, 0, 1)); break;
            case Keys.Left: if (_duration > TimeSpan.Zero) SeekFractionRequested?.Invoke(Math.Clamp((Now + _offset - TimeSpan.FromSeconds(5)).TotalSeconds / _duration.TotalSeconds, 0, 1)); break;
            case Keys.Up: _volume = Math.Clamp(_volume + 0.05, 0, 1); VolumeRequested?.Invoke(_volume); break;
            case Keys.Down: _volume = Math.Clamp(_volume - 0.05, 0, 1); VolumeRequested?.Invoke(_volume); break;
            default: return;
        }
        e.Handled = true;
        Invalidate();
    }

    // ---- host → stage ------------------------------------------------------------
    /// <summary>The song and its cover (ownership of <paramref name="cover"/> passes to the stage).</summary>
    public void SetTrack(string title, string artist, Bitmap? cover, string format = "")
    {
        _title = title; _artist = artist; _format = format;
        var old = _cover; _cover = cover; old?.Dispose();
        // Keep what was on screen so the new picture can dissolve over it instead of snapping in.
        DropOld();
        if (Visible && (_washA is not null || _coverScaled is not null) && Anim.MotionEnabled)
        {
            _oldWashA = _washA; _oldWashB = _washB; _oldCoverScaled = _coverScaled;
            _washA = _washB = null; _coverScaled = null;
            _swapT0 = _clock.Elapsed.TotalMilliseconds;
        }
        else { _coverScaled?.Dispose(); _coverScaled = null; }
        BuildWash();
        Invalidate();
    }

    public void SetLyrics(IReadOnlyList<LyricLine> lines, bool synced, string status)
    {
        _lines = lines; _synced = synced; _status = status;
        _measuredFor = -1; _scroll = 0; _userScroll = 0; _landed = false;
        _shownCur = -1; _prevCur = -1; _handoffT0 = 0; _revealT0 = _clock.Elapsed.TotalMilliseconds;
        Invalidate();
    }

    public void SetPosition(TimeSpan pos, TimeSpan duration, bool playing)
    {
        _reported = pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        _duration = duration; _playing = playing;
        _since.Restart();
        Invalidate();
    }

    public void SetOffsetMs(int ms) { _offset = TimeSpan.FromMilliseconds(ms); Invalidate(); }
    public void SetVolume(double v) { if (!_volDrag) _volume = Math.Clamp(v, 0, 1); }

    public void AnimateIn()
    {
        _introTween?.Cancel();
        if (!Anim.MotionEnabled) { _intro = 1f; Invalidate(); return; }
        _intro = 0f;
        _introTween = Anim.Run(320, v => { _intro = (float)v; if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    public void AnimateOut(Action done)
    {
        _introTween?.Cancel();
        if (!Anim.MotionEnabled) { done(); return; }
        float from = _intro;
        _introTween = Anim.Run(180, v => { _intro = from * (1f - (float)v); if (!IsDisposed) Invalidate(); }, done, Easings.OutCubic);
    }

    private void ToggleSheet()
    {
        double from = _sheetShown, to = _sheetShown < 0.5 ? 1 : 0;
        _sheetTween?.Cancel();
        _sheetTween = Anim.Run(360, v => { _sheetShown = from + (to - from) * v; if (!IsDisposed) Invalidate(); }, () => _sheetTween = null, Easings.InOutCubic);
    }

    private TimeSpan Now
    {
        get
        {
            var pos = _reported;
            if (_playing) { var s = _since.Elapsed; pos += s > MaxDrift ? MaxDrift : s; }
            return pos - _offset;
        }
    }

    // ---- geometry ------------------------------------------------------------------
    private const int Pad = 40;
    /// <summary>The cover's column: 42 % of the window with the words showing, the whole window without.</summary>
    private int LeftW => (int)Math.Round(Math.Clamp(Width * 0.42, 280, 560) * _sheetShown + Width * (1 - _sheetShown));
    private int CoverSize => Math.Max(140, Math.Min(Math.Clamp((int)(Width * 0.42), 280, 560) - 2 * Pad, Height - (UnderDeck ? 170 : 290)));
    private Rectangle CoverRect
    {
        get
        {
            int s = CoverSize, block = s + (UnderDeck ? 70 : 190);   // cover + title/artist (+ times + seek + transport + volume in the full stage)
            return new Rectangle((LeftW - s) / 2, Math.Max(Pad, (Height - block) / 2), s, s);
        }
    }
    /// <summary>Title and artist sit under the cover — the bar that used to name the song is under us.</summary>
    private Rectangle TitleRect { get { var c = CoverRect; return new Rectangle(c.X, c.Bottom + 18, c.Width, 22); } }
    private Rectangle ArtistRect { get { var c = CoverRect; return new Rectangle(c.X, c.Bottom + 40, c.Width, 18); } }
    private Rectangle SeekRect { get { var c = CoverRect; return new Rectangle(c.X, c.Bottom + 82, c.Width, 4); } }
    private Rectangle TransportRect { get { var c = CoverRect; return new Rectangle(c.X, SeekRect.Bottom + 16, c.Width, 44); } }
    private Rectangle PrevRect { get { var t = TransportRect; int cx = t.X + t.Width / 2; return new Rectangle(cx - 22 - 54, t.Y, 44, 44); } }
    private Rectangle PlayRect { get { var t = TransportRect; int cx = t.X + t.Width / 2; return new Rectangle(cx - 22, t.Y, 44, 44); } }
    private Rectangle NextRect { get { var t = TransportRect; int cx = t.X + t.Width / 2; return new Rectangle(cx - 22 + 54, t.Y, 44, 44); } }
    private Rectangle BubbleRect { get { var t = TransportRect; return new Rectangle(t.Right - 36, t.Y + 6, 32, 32); } }
    private Rectangle MoreRect { get { var t = TransportRect; return new Rectangle(t.X + 4, t.Y + 6, 32, 32); } }
    /// <summary>Where the host should anchor the popover it opens for "···".</summary>
    public Rectangle MoreAnchorScreen => RectangleToScreen(UnderDeck ? CornerRect : MoreRect);
    private Rectangle CornerRect => new(Width - 22 - 30, 18, 30, 30);   // top-right: "···" under the deck, close in the full stage
    private Rectangle VolRect { get { var t = TransportRect; return new Rectangle(t.X + 30, t.Bottom + 22, t.Width - 30, 4); } }
    private Rectangle SpeakerRect { get { var v = VolRect; return new Rectangle(v.X - 30, v.Y - 9, 22, 22); } }
    private Rectangle CloseRect => new(Width - 22 - 30, 18, 30, 30);
    // The words never run wider than a comfortable column, however wide the window — Apple caps it too.
    private Rectangle SheetRect => new(LeftW + 16, 0, Math.Clamp(Width - LeftW - 16 - Pad, 100, 760), Height);
    private double ContentHeight => _rows.Length == 0 ? 0 : _rows[^1].Top + _rows[^1].Height;

    private Hit HitAt(Point p)
    {
        if (UnderDeck) return CornerRect.Contains(p) ? Hit.More : Hit.None;   // the deck owns the transport; only "···" and the words are ours
        if (CloseRect.Contains(p)) return Hit.Close;
        if (PrevRect.Contains(p)) return Hit.Prev;
        if (PlayRect.Contains(p)) return Hit.Play;
        if (NextRect.Contains(p)) return Hit.Next;
        if (BubbleRect.Contains(p)) return Hit.Bubble;
        if (MoreRect.Contains(p)) return Hit.More;
        var s = SeekRect; s.Inflate(0, 8);
        if (s.Contains(p)) return Hit.Seek;
        var v = VolRect; v.Inflate(0, 8);
        if (v.Contains(p)) return Hit.Volume;
        return Hit.None;
    }

    private double SeekFrac(int x) => Math.Clamp((x - SeekRect.X) / (double)Math.Max(1, SeekRect.Width), 0, 1);
    private double VolFrac(int x) => Math.Clamp((x - VolRect.X) / (double)Math.Max(1, VolRect.Width), 0, 1);

    // ---- the wash --------------------------------------------------------------------
    private void BuildWash()
    {
        _washA?.Dispose(); _washB?.Dispose(); _washA = _washB = null;
        if (_cover is null) return;
        try
        {
            // A 10×10 reading of the cover, blown up with bilinear filtering, IS a blur — the same soft field
            // Apple animates behind its lyrics, with none of the cost of a real one.
            _washA = Soft(_cover, 10, RotateFlipType.RotateNoneFlipNone);
            _washB = Soft(_cover, 6, RotateFlipType.Rotate180FlipNone);
        }
        catch { _washA?.Dispose(); _washB?.Dispose(); _washA = _washB = null; }
    }

    private static Bitmap Soft(Bitmap src, int cells, RotateFlipType flip)
    {
        using var tiny = new Bitmap(cells, cells);
        using (var g = Graphics.FromImage(tiny))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, cells, cells);
        }
        tiny.RotateFlip(flip);
        // Push the colour a little: averaging a cover into a few cells greys it, and the wash should carry the
        // cover's mood, not its mean. (A hundred pixels — GetPixel/SetPixel is fine here.)
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                var p = tiny.GetPixel(x, y);
                double l = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                int R = (int)Math.Clamp(l + (p.R - l) * 1.35, 0, 255), G = (int)Math.Clamp(l + (p.G - l) * 1.35, 0, 255), B = (int)Math.Clamp(l + (p.B - l) * 1.35, 0, 255);
                tiny.SetPixel(x, y, Color.FromArgb(R, G, B));
            }
        var soft = new Bitmap(220, 220);
        using (var g = Graphics.FromImage(soft))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Draw a little larger than the target so the bilinear edge of the tiny source never shows.
            g.DrawImage(tiny, new Rectangle(-30, -30, 280, 280));
        }
        return soft;
    }

    /// <summary>0..1 through the track-change dissolve; 1 when nothing is dissolving.</summary>
    private float Swap => _oldWashA is null && _oldCoverScaled is null ? 1f
        : (float)Math.Clamp((_clock.Elapsed.TotalMilliseconds - _swapT0) / SwapMs, 0.0, 1.0);

    private void DropOld()
    {
        _oldWashA?.Dispose(); _oldWashB?.Dispose(); _oldCoverScaled?.Dispose();
        _oldWashA = _oldWashB = _oldCoverScaled = null;
    }

    private Bitmap? _washFrame;

    private void DrawWash(Graphics g)
    {
        float swap = Swap;
        if (swap >= 1f && _oldWashA is not null) DropOld();
        if (_washA is null && _oldWashA is null) { using var b0 = new SolidBrush(Theme.Bg); g.FillRectangle(b0, ClientRectangle); return; }

        // Compose the wash at ONE THIRD of the window, then scale it up once. It is a blur — a third of the
        // pixels look identical — and drawing two 220 px fields up to a 2000 px canvas with high-quality
        // filtering twice a frame was most of what made the sheet stutter.
        int fw = Math.Max(48, Width / 3), fh = Math.Max(48, Height / 3);
        if (_washFrame is null || _washFrame.Width != fw || _washFrame.Height != fh) { _washFrame?.Dispose(); _washFrame = new Bitmap(fw, fh); }
        double t = _clock.Elapsed.TotalSeconds;
        using (var fg = Graphics.FromImage(_washFrame))
        {
            fg.InterpolationMode = InterpolationMode.Bilinear;
            fg.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            fg.CompositingQuality = CompositingQuality.HighSpeed;
            using (var b = new SolidBrush(Theme.Bg)) fg.FillRectangle(b, 0, 0, fw, fh);
            float k = Style == WashStyle.Poster ? 1f : Style == WashStyle.Tint ? 0.42f : 0.62f;   // the colour's strength over the card surface
            if (swap < 1f && _oldWashA is not null) DrawWashLayers(fg, _oldWashA, _oldWashB, t, k, fw, fh);
            if (_washA is not null) DrawWashLayers(fg, _washA, _washB, t, k * swap, fw, fh);
            if (Style == WashStyle.Glow)
            {
                // The colour belongs to the cover: from a third of the way across it fades back to the card's own
                // surface, so the words sit on the same dark ground as every other page.
                using var gb = new LinearGradientBrush(new Rectangle(-1, 0, fw + 2, fh), Color.FromArgb(0, Theme.Bg), Color.FromArgb(230, Theme.Bg), 0f);
                gb.Blend = new Blend { Positions = new[] { 0f, 0.28f, 0.78f, 1f }, Factors = new[] { 0f, 0f, 1f, 1f } };
                fg.FillRectangle(gb, 0, 0, fw, fh);
            }
            // A light scrim: enough for white words to read on a vivid cover, not so much that the colour dies.
            using var scrim = new SolidBrush(Color.FromArgb(Style == WashStyle.Poster ? 64 : 36, 0, 0, 0));
            fg.FillRectangle(scrim, 0, 0, fw, fh);
        }
        var im = g.InterpolationMode; var po = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_washFrame, new Rectangle(0, 0, Width, Height));
        g.InterpolationMode = im; g.PixelOffsetMode = po;
    }

    private void DrawWashLayers(Graphics g, Bitmap a, Bitmap? b, double t, float alpha, int Width, int Height)
    {
        // Oversize and drift: two periods that never line up, so it breathes instead of looping.
        int ow = (int)(Width * 1.5), oh = (int)(Height * 1.5);
        float ax = (float)(-(ow - Width) / 2 + Math.Sin(t / 11.0) * Width * 0.12);
        float ay = (float)(-(oh - Height) / 2 + Math.Cos(t / 17.0) * Height * 0.10);
        if (alpha >= 1f) g.DrawImage(a, new RectangleF(ax, ay, ow, oh));
        else Theme.DrawImageAlpha(g, a, new RectangleF(ax, ay, ow, oh), alpha);
        if (b is null) return;
        // The second field also turns, very slowly, about the middle of the window — a drift that only
        // slides reads as a picture being dragged; a drift that also turns reads as light moving.
        float bx = (float)(-(ow - Width) / 2 + Math.Cos(t / 13.0) * Width * 0.16);
        float by = (float)(-(oh - Height) / 2 + Math.Sin(t / 9.0) * Height * 0.14);
        var st = g.Save();
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.RotateTransform((float)(t * 1.2 % 360));
        g.TranslateTransform(-Width / 2f, -Height / 2f);
        Theme.DrawImageAlpha(g, b, new RectangleF(bx - Width * 0.25f, by - Height * 0.25f, ow * 1.35f, oh * 1.35f), 0.55f * alpha);
        g.Restore(st);
    }

    // ---- painting ------------------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;   // not GridFit: it snaps glyphs and shimmers under the pop
        EnsureFonts();
        MainForm.Trace("paint: wash");
        DrawWash(g);

        float a = _intro;                       // the open/close: everything rises and fades together
        int rise = (int)Math.Round(10 * (1 - a));
        g.TranslateTransform(0, rise);
        MainForm.Trace("paint: left");
        DrawLeft(g, a);
        MainForm.Trace("paint: sheet");
        if (_sheetShown > 0.01) DrawSheet(g, (float)(a * _sheetShown));
        g.ResetTransform();
        MainForm.Trace("paint: chrome");
        if (UnderDeck)
        {
            DrawMore(g, CornerRect, _hot == Hit.More, a);
            Theme.CarveCardCorners(g, this, Theme.RadShell, true, true, true, true);   // it IS the content card: same corners
        }
        else DrawClose(g, a);
    }

    private static Color W(double alpha) => Color.FromArgb(Math.Clamp((int)Math.Round(alpha * 255), 0, 255), 255, 255, 255);

    /// <summary>The words scale with the window: 24 pt in a small one, up to 32 pt on a big screen.</summary>
    private void EnsureFonts()
    {
        float want = Math.Clamp(Height / 24f, 22f, 32f);
        if (_fLine is not null && Math.Abs(want - _lineSize) < 0.75f) return;
        _lineSize = want;
        _fLine = FontFor(want);
        _measuredFor = -1;
    }

    private Font FontFor(float size)
    {
        int key = (int)Math.Round(size * 10);
        if (!_fontCache.TryGetValue(key, out var f)) { f = Theme.DisplayFont(key / 10f, FontStyle.Bold); _fontCache[key] = f; }
        return f;
    }

    private void DrawLeft(Graphics g, float a)
    {
        var r = CoverRect;
        int rad = Math.Max(8, (int)Math.Round(r.Width * Theme.TileFrac));
        for (int i = 6; i >= 1; i--)   // a deep, soft shadow lifts the cover off the wash
            using (var sh = new SolidBrush(Color.FromArgb((int)(18 * a), 0, 0, 0)))
            using (var sp = Theme.RoundedRect(new RectangleF(r.X - i, r.Y + i + 4, r.Width + i * 2, r.Height + i * 2), rad + i))
                g.FillPath(sh, sp);

        using (var clip = Theme.RoundedRect(new RectangleF(r.X, r.Y, r.Width, r.Height), rad))
        {
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Intersect);
            if (_cover is not null)
            {
                if (_coverScaled is null || _coverScaled.Width != r.Width)
                {
                    _coverScaled?.Dispose();
                    _coverScaled = new Bitmap(r.Width, r.Height);
                    using var cg = Graphics.FromImage(_coverScaled);
                    cg.InterpolationMode = InterpolationMode.HighQualityBicubic; cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    cg.DrawImage(_cover, 0, 0, r.Width, r.Height);
                }
                float swap = Swap;
                if (swap < 1f && _oldCoverScaled is not null && _oldCoverScaled.Width == r.Width)
                    Theme.DrawImageAlpha(g, _oldCoverScaled, r, a);          // the old one under…
                Theme.DrawImageAlpha(g, _coverScaled, r, a * (swap < 1f ? swap : 1f));   // …the new one dissolving in
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb((int)(40 * a), 255, 255, 255));
                g.FillRectangle(ph, r);
                float ns = r.Width * 0.28f;
                Theme.DrawNote(g, new RectangleF(r.X + (r.Width - ns) / 2f, r.Y + (r.Height - ns) / 2f, ns, ns), W(0.5 * a));
            }
            g.Clip = saved;
            using var edge = new Pen(W(0.16 * a));
            g.DrawPath(edge, clip);
        }

        // title + artist
        using (var tb = new SolidBrush(W(0.95 * a)))
        using (var ab = new SolidBrush(W(0.6 * a)))
        // No LineLimit here: with it, a line that does not fit the rect's height is not clipped but DROPPED
        // — a 22 px rect and a 13 pt bold face left the title invisible.
        using (var fmt = new StringFormat(Typo) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            var t = TitleRect; var ar = ArtistRect;
            g.DrawString(_title, _fTitle, tb, new RectangleF(t.X, t.Y, t.Width, t.Height + 8), fmt);
            g.DrawString(_artist, _fSub, ab, new RectangleF(ar.X, ar.Y, ar.Width, ar.Height + 6), fmt);
            // The format chip (Tidal / Plexamp): codec · bitrate · sample rate. Under the deck it takes the line below
            // the artist; in the full stage the times sit there, so it goes after the artist's name when it fits.
            if (_format.Length > 0)
            {
                var sz = g.MeasureString(_format, _fTime, PointF.Empty, Typo);
                float cw = sz.Width + 16, ch = 20;
                RectangleF chip;
                if (UnderDeck) chip = new RectangleF(ar.X, ar.Bottom + 8, cw, ch);
                else
                {
                    float aw = Math.Min(ar.Width, g.MeasureString(_artist, _fSub, PointF.Empty, Typo).Width);
                    chip = new RectangleF(ar.X + aw + 12, ar.Y - 1, cw, ch);
                    if (chip.Right > ar.Right) chip = RectangleF.Empty;
                }
                if (!chip.IsEmpty)
                {
                    using var op = new Pen(W(0.22 * a));
                    using var cb = new SolidBrush(W(0.55 * a));
                    using var cp = Theme.RoundedRect(chip, 6f);
                    g.DrawPath(op, cp);
                    g.DrawString(_format, _fTime, cb, chip.X + 8, chip.Y + (ch - sz.Height) / 2f, Typo);
                }
            }
        }

        if (UnderDeck) return;   // the deck shows the times and owns the transport

        // times + seek
        var s = SeekRect;
        double dur = _duration.TotalSeconds, pos = Math.Clamp((_reported + (_playing ? _since.Elapsed : TimeSpan.Zero)).TotalSeconds, 0, Math.Max(0, dur));
        using (var tb = new SolidBrush(W(0.55 * a)))
        {
            g.DrawString(Fmt(pos), _fTime, tb, s.X, s.Y - 18, Typo);
            string rem = dur > 0 ? "-" + Fmt(dur - pos) : "";
            var sz = g.MeasureString(rem, _fTime, PointF.Empty, Typo);
            g.DrawString(rem, _fTime, tb, s.Right - sz.Width, s.Y - 18, Typo);
        }
        DrawSlider(g, s, dur > 0 ? (float)(pos / dur) : 0, _hot == Hit.Seek || _seeking, a);

        // transport — white glyphs on the wash, the way Apple draws them
        DrawSkip(g, PrevRect, prev: true, _hot == Hit.Prev, a);
        DrawPlay(g, PlayRect, _hot == Hit.Play, a);
        DrawSkip(g, NextRect, prev: false, _hot == Hit.Next, a);
        DrawBubble(g, BubbleRect, _hot == Hit.Bubble, _sheetShown, a);
        DrawMore(g, MoreRect, _hot == Hit.More, a);

        // volume — the bar is under us, so this is the only one there is
        DrawSpeaker(g, SpeakerRect, a);
        DrawSlider(g, VolRect, (float)_volume, _hot == Hit.Volume || _volDrag, a);
    }

    private static void DrawSlider(Graphics g, Rectangle s, float f, bool hot, float a)
    {
        using var track = new SolidBrush(W(0.28 * a));
        using var fill = new SolidBrush(W(0.92 * a));
        using (var tp = Theme.RoundedRect(new RectangleF(s.X, s.Y, s.Width, s.Height), 2)) g.FillPath(track, tp);
        if (f > 0)
        {
            using var fp = Theme.RoundedRect(new RectangleF(s.X, s.Y, Math.Max(4, s.Width * f), s.Height), 2);
            g.FillPath(fill, fp);
        }
        if (hot)
        {
            float kx = s.X + s.Width * f;
            g.FillEllipse(fill, kx - 6, s.Y + s.Height / 2f - 6, 12, 12);
        }
    }

    private static string Fmt(double sec)
    {
        int t = (int)Math.Round(sec);
        return $"{t / 60}:{t % 60:00}";
    }

    private static void DrawSkip(Graphics g, Rectangle r, bool prev, bool hot, float a)
    {
        if (hot) { using var hb = new SolidBrush(W(0.12 * a)); g.FillEllipse(hb, r); }
        using var b = new SolidBrush(W((hot ? 1.0 : 0.85) * a));
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        float s = 8f, d = prev ? -1 : 1;
        g.FillPolygon(b, new[] { new PointF(m.X + d * 1, m.Y - s), new PointF(m.X + d * 1, m.Y + s), new PointF(m.X + d * (s + 2), m.Y) });
        g.FillPolygon(b, new[] { new PointF(m.X - d * (s + 1), m.Y - s), new PointF(m.X - d * (s + 1), m.Y + s), new PointF(m.X + d * 1, m.Y) });
    }

    private void DrawPlay(Graphics g, Rectangle r, bool hot, float a)
    {
        if (hot) { using var hb = new SolidBrush(W(0.12 * a)); g.FillEllipse(hb, r); }
        using var b = new SolidBrush(W((hot ? 1.0 : 0.92) * a));
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        // Bigger than the skips: the one button that matters most, as Apple sizes it.
        if (_playing)
        {
            float bw = 6f, gap = 6f, bh = 24;
            g.FillRectangle(b, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh);
            g.FillRectangle(b, c.X + gap / 2, c.Y - bh / 2, bw, bh);
        }
        else
        {
            float s = 13f;
            g.FillPolygon(b, new[] { new PointF(c.X - s + 3, c.Y - s), new PointF(c.X - s + 3, c.Y + s), new PointF(c.X + s + 3, c.Y) });
        }
    }

    /// <summary>Apple's lyrics toggle: a speech bubble with two lines, filled while the words are showing.</summary>
    private static void DrawBubble(Graphics g, Rectangle r, bool hot, double on, float a)
    {
        if (hot) { using var hb = new SolidBrush(W(0.12 * a)); g.FillEllipse(hb, r); }
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        var bubble = new Rectangle(cx - 9, cy - 8, 18, 13);
        using var path = Theme.RoundedRect(bubble, 4);
        using var tail = new GraphicsPath();
        tail.AddLines(new[] { new Point(cx - 5, bubble.Bottom - 1), new Point(cx - 6, cy + 8), new Point(cx, bubble.Bottom - 1) });
        double lit = 0.5 + 0.5 * on;
        using (var fill = new SolidBrush(W(0.16 * on * a))) { g.FillPath(fill, path); g.FillPath(fill, tail); }
        using var pen = new Pen(W(lit * (hot ? 1 : 0.85) * a), 1.5f) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.DrawLines(pen, new[] { new Point(cx - 5, bubble.Bottom), new Point(cx - 6, cy + 8), new Point(cx, bubble.Bottom) });
        using var line = new Pen(W(lit * a), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line, cx - 5, cy - 4, cx + 4, cy - 4);
        g.DrawLine(line, cx - 5, cy - 1, cx + 6, cy - 1);
    }

    /// <summary>Apple's "···" — the way to the things that do not belong on the stage itself.</summary>
    private static void DrawMore(Graphics g, Rectangle r, bool hot, float a)
    {
        if (hot) { using var hb = new SolidBrush(W(0.12 * a)); g.FillEllipse(hb, r); }
        using var b = new SolidBrush(W((hot ? 1.0 : 0.85) * a));
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        for (int k = -1; k <= 1; k++) g.FillEllipse(b, cx + k * 7 - 2f, cy - 2f, 4f, 4f);
    }

    private void DrawSpeaker(Graphics g, Rectangle r, float a)
    {
        using var b = new SolidBrush(W(0.7 * a));
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        g.FillPolygon(b, new[] { new Point(cx - 8, cy - 3), new Point(cx - 4, cy - 3), new Point(cx + 1, cy - 7), new Point(cx + 1, cy + 7), new Point(cx - 4, cy + 3), new Point(cx - 8, cy + 3) });
        using var p = new Pen(W(0.7 * a * (_volume > 0.02 ? 1 : 0.35)), 1.5f);
        g.DrawArc(p, cx - 1, cy - 5, 10, 10, -45, 90);
        if (_volume > 0.5) g.DrawArc(p, cx - 1, cy - 9, 18, 18, -45, 90);
    }

    private void DrawClose(Graphics g, float a)
    {
        var r = CloseRect;
        bool hot = _hot == Hit.Close;
        if (hot) { using var hb = new SolidBrush(W(0.12 * a)); g.FillEllipse(hb, r); }
        using var p = new Pen(W((hot ? 0.95 : 0.6) * a), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // "collapse" — two arrow heads pointing in, the inverse of the popover's expand mark
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        g.DrawLine(p, cx - 7, cy - 7, cx - 2, cy - 2); g.DrawLine(p, cx - 2, cy - 7, cx - 2, cy - 2); g.DrawLine(p, cx - 7, cy - 2, cx - 2, cy - 2);
        g.DrawLine(p, cx + 7, cy + 7, cx + 2, cy + 2); g.DrawLine(p, cx + 2, cy + 7, cx + 2, cy + 2); g.DrawLine(p, cx + 7, cy + 2, cx + 2, cy + 2);
    }

    // ---- the sheet -----------------------------------------------------------------------
    // Layout is measured ONCE per width; a row keeps its wrapped visual lines as ready-to-draw strings, so a
    // frame draws one DrawString per visual line instead of one per WORD — that alone took the stage from
    // ~300 text calls a frame to ~15.
    private void Measure(Graphics g)
    {
        var f = _fLine!;
        int maxW = (int)(SheetRect.Width / Pop);     // a little narrower, so the grown sung line still fits
        if (_measuredFor == maxW && _rows.Length == _lines.Count) return;
        _rows = new Row[_lines.Count];
        int lineH = (int)Math.Ceiling(g.MeasureString("Xg", f, PointF.Empty, Typo).Height * 1.08);
        int gap = (int)(lineH * 0.55), y = 0;
        float space = g.MeasureString(" ", f, PointF.Empty, Typo).Width;
        for (int i = 0; i < _lines.Count; i++)
        {
            var row = new Row { Top = y, LineH = lineH };
            string text = _lines[i].Text;
            if (text.Length == 0)
            {
                double gapS = i + 1 < _lines.Count ? (_lines[i + 1].At - _lines[i].At).TotalSeconds : 0;
                row.Beat = gapS >= BeatSeconds;
                row.Height = row.Beat ? lineH : lineH / 2;
                _rows[i] = row; y += row.Height + gap; continue;
            }
            float x = 0; int visual = 0;
            var cur = new System.Text.StringBuilder();
            foreach (string w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                float wd = g.MeasureString(w, f, PointF.Empty, Typo).Width;
                if (x > 0 && x + wd > maxW) { row.Lines.Add(cur.ToString()); cur.Clear(); x = 0; visual++; }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(w);
                row.Words.Add(new Word { W = w, VisualLine = visual, Wd = wd });
                x += wd + space;
            }
            if (cur.Length > 0) row.Lines.Add(cur.ToString());
            row.Height = (visual + 1) * lineH;
            _rows[i] = row;
            y += row.Height + gap;
        }
        _space = space;
        _measuredFor = maxW;
    }
    private float _space = 8;

    private int LineAt(Point p)
    {
        if (_lines.Count == 0 || _sheetShown < 0.5 || !SheetRect.Contains(p)) return -1;
        double y = p.Y + _scroll + _userScroll;
        for (int i = 0; i < _rows.Length; i++)
            if (_rows[i].Words.Count > 0 && y >= _rows[i].Top && y < _rows[i].Top + _rows[i].Height) return i;
        return -1;
    }

    private double FocusY => Height * 0.42;

    private double LineProgress(int i, TimeSpan now)
    {
        if (i < 0 || i >= _lines.Count) return 0;
        TimeSpan end = i + 1 < _lines.Count ? _lines[i + 1].At : _lines[i].At + TimeSpan.FromSeconds(5);
        double span = (end - _lines[i].At).TotalSeconds;
        return span <= 0.01 ? 1 : Math.Clamp((now - _lines[i].At).TotalSeconds / span, 0, 1);
    }

    private void DrawSheet(Graphics g, float a)
    {
        var sr = SheetRect;
        if (_lines.Count == 0)
        {
            using var sb = new SolidBrush(W(0.6 * a));
            using var fmt = new StringFormat(Typo) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            g.DrawString(_status, _fSub, sb, new RectangleF(sr.X, 0, sr.Width, Height), fmt);
            return;
        }
        Measure(g);
        var now = Now;
        int cur = _synced ? LyricsLookup.IndexAt(_lines, now) : -1;
        if (cur != _shownCur) { _prevCur = _shownCur; _shownCur = cur; _handoffT0 = _clock.Elapsed.TotalMilliseconds; }
        double ho = !Anim.MotionEnabled || _handoffT0 == 0 ? 1 : Math.Clamp((_clock.Elapsed.TotalMilliseconds - _handoffT0) / HandoffMs, 0, 1);
        double reveal = !Anim.MotionEnabled ? 1 : Math.Clamp((_clock.Elapsed.TotalMilliseconds - _revealT0) / 260.0, 0, 1);
        double t = Easings.OutCubic(ho);   // ONE curve for the whole hand-off: colour and size move together

        // ---- scroll ----
        // Only a followed sheet has a home to ease back to; an untimed one stays where the reader left it.
        if (_synced && _clock.Elapsed.TotalMilliseconds - _lastUserScrollTick > 4000) _userScroll *= 0.86;
        int aim = cur;
        while (aim >= 0 && aim < _rows.Length && _rows[aim].Words.Count == 0) aim++;
        if (aim >= _rows.Length) { aim = cur; while (aim > 0 && _rows[aim].Words.Count == 0) aim--; }
        if (_synced && cur >= 0 && cur < _rows.Length && _rows[cur].Beat && cur + 1 < _lines.Count
            && (_lines[cur + 1].At - now).TotalSeconds > 0.9) aim = cur;
        double dt = Math.Min(0.05, _frameTime.Elapsed.TotalSeconds);
        _frameTime.Restart();
        if (_synced)
        {
            int tt = cur >= 0 ? aim : 0;
            double target = _rows[tt].Top;
            if (cur >= 0 && tt == cur && cur + 1 < _rows.Length)
                target += (_rows[cur + 1].Top - _rows[cur].Top) * LineProgress(cur, now);
            target -= FocusY;
            if (!_landed) { _scroll = target; _landed = true; }
            else _scroll += (target - _scroll) * (1 - Math.Exp(-dt / 0.22));
        }
        double top = _scroll + _userScroll;

        g.SetClip(new Rectangle(sr.X - 8, 0, sr.Width + 16, Height));

        // intro dots
        if (_synced && cur < 0 && _rows.Length > 0 && _lines[0].At.TotalSeconds >= BeatSeconds)
        {
            int y0 = (int)Math.Round(_rows[0].Top - top);
            DrawBeat(g, sr.X, y0 - (int)(_rows[0].LineH * 0.7), now.TotalSeconds / _lines[0].At.TotalSeconds, now.TotalSeconds, reveal * a);
        }

        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            int y = (int)Math.Round(row.Top - top);
            if (y + row.Height < 0) continue;
            if (y > Height) break;
            if (row.Words.Count == 0)
            {
                if (row.Beat && _synced && i == cur && i + 1 < _lines.Count)
                {
                    double span = (_lines[i + 1].At - _lines[i].At).TotalSeconds;
                    DrawBeat(g, sr.X, y + row.Height / 2, span <= 0 ? 1 : (now - _lines[i].At).TotalSeconds / span, now.TotalSeconds, reveal * a);
                }
                continue;
            }

            bool isNow = _synced && i == cur, isPast = _synced && i < cur;
            bool isPrev = _synced && i == _prevCur && ho < 1 && !isNow;
            double d = Math.Clamp(Math.Abs(y + row.Height / 2.0 - FocusY) / (Height * 0.55), 0, 1);
            // Every resting line sits BELOW the sung line: the line being sung is the brightest thing on the
            // sheet, and the eye never drops to the next one.
            double rest = _synced ? 0.50 - 0.36 * Math.Pow(d, 1.1) : 0.62;
            if (isPast) rest *= 0.8;
            if (i == _hover && !isNow) rest = Math.Min(0.8, rest + 0.2);
            // The words melt into the top and bottom edges by fading THEMSELVES — a darkening band over the
            // wash reads as a dirty stripe on a coloured ground, so nothing is painted over the picture.
            double edge = Math.Clamp(Math.Min(y + row.Height - 10, Height - y - 10) / 90.0, 0, 1);

            // WHOLE-LINE highlight. The sung line brightens and grows as one thing over the hand-off, the
            // line before it dims and settles back. No word-by-word fill: the sources give LINE times, and a
            // per-word guess read as jitter rather than as karaoke.
            double alpha; float scale = 1f;
            if (isNow) { alpha = rest + (1 - rest) * t; scale = 1 + (Pop - 1) * (float)t; }
            else if (isPrev) { alpha = 1 - (1 - rest) * t; scale = 1 + (Pop - 1) * (1 - (float)t); }
            else alpha = rest;
            alpha *= reveal * a * edge;

            if (i == _hover && _synced && _sheetShown > 0.5)
            {
                using var hp = Theme.RoundedRect(new RectangleF(sr.X - 14, y - 8, Math.Min(sr.Width, RowWidth(row)) + 28, row.Height + 16), 10);
                using var hb = new SolidBrush(W(0.10 * reveal * a * edge));
                g.FillPath(hb, hp);
            }
            DrawRowLines(g, row, y, W(alpha), scale);
        }
        g.ResetClip();
    }

    /// <summary>The widest visual line of a row, for the hover slab.</summary>
    private float RowWidth(Row row)
    {
        float best = 0, x = 0; int line = 0;
        foreach (var w in row.Words)
        {
            if (w.VisualLine != line) { line = w.VisualLine; x = 0; }
            x += w.Wd + _space;
            best = Math.Max(best, x - _space);
        }
        return best;
    }

    /// <summary>Draw a row's visual lines in one colour. The sung line's growth is a SCALE TRANSFORM about
    /// the row's left edge, not a bigger font: GDI+ text is vector under a transform, so the size moves
    /// continuously — a per-frame font size stepped through a handful of cached fonts and visibly jumped.</summary>
    private void DrawRowLines(Graphics g, Row row, int y, Color col, float scale)
    {
        using var b = new SolidBrush(col);
        var st = g.Save();
        if (Math.Abs(scale - 1f) > 0.002f)
        {
            g.TranslateTransform(SheetRect.X, y);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-SheetRect.X, -y);
        }
        for (int k = 0; k < row.Lines.Count; k++)
            g.DrawString(row.Lines[k], _fLine!, b, SheetRect.X, y + k * row.LineH, Typo);
        g.Restore(st);
    }

    private static void DrawBeat(Graphics g, int x, int cy, double prog, double t, double alpha)
    {
        prog = Math.Clamp(prog, 0, 1);
        for (int k = 0; k < 3; k++)
        {
            double f = Math.Clamp(prog * 3 - k, 0, 1);
            double breathe = Anim.MotionEnabled ? 0.9 * Math.Sin(2 * Math.PI * t / 1.9 - k * 0.7) : 0;
            float r = (float)(5.5 + breathe);
            using var b = new SolidBrush(W((0.30 + 0.70 * f) * alpha));
            g.FillEllipse(b, x + 6 + k * 20 - r, cy - r, r * 2, r * 2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _introTween?.Cancel(); _sheetTween?.Cancel();
            _clockPump?.Dispose(); _clockPump = null;
            _cover?.Dispose(); _coverScaled?.Dispose(); _washA?.Dispose(); _washB?.Dispose(); _washFrame?.Dispose();
            DropOld();
            foreach (var f in _fontCache.Values) f.Dispose();
            _fontCache.Clear();
            _fTime.Dispose(); _fSub.Dispose(); _fTitle.Dispose();
        }
        base.Dispose(disposing);
    }
}
