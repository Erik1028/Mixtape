using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The live-lyrics sheet. The line being sung fills word by word as it is sung — the karaoke read — while
/// the sheet glides continuously rather than jumping line to line, and the rest of the words fade away with
/// distance. Clicking a line seeks to it.
///
/// Two things make it feel live rather than ticky:
///   • The player only reports its position a few times a second, so the panel INTERPOLATES between reports
///     with a stopwatch. Everything on screen is a function of that smooth clock.
///   • Nothing here snaps. Scrolling is an exponential glide toward the target computed every frame, which
///     is frame-rate independent, so it stays smooth whatever the paint rate turns out to be.
///
/// It owns one 60 fps timer that runs ONLY while the panel is visible and a song is playing; it fetches
/// nothing and blocks nothing — the host hands it lines that are already resolved.
/// </summary>
internal sealed class LyricsPanel : Control
{
    public event Action? CloseRequested;
    public event Action<TimeSpan>? SeekRequested;   // clicked a line → jump to it
    public event Action<int>? OffsetChanged;        // the user nudged the sync (milliseconds)
    public event Action? VersionsRequested;        // "wrong words?" — go and ask what else exists
    public event Action<LyricsCandidate>? VersionChosen;
    public event Action? ExpandRequested;           // open the full-window view

    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private bool _synced;
    private string _status = "";
    private string _songLine = "";

    // ---- the smooth clock -------------------------------------------------
    private TimeSpan _reported;                     // last position the player told us
    private readonly Stopwatch _since = new();      // time since that report
    private bool _playing;

    // ---- scroll ----------------------------------------------------------
    // ---- the user's own sync nudge ---------------------------------------
    // Community lyric sheets are timed against ONE copy of a recording. A different master, a radio edit,
    // or a rip with a longer intro shifts the whole sheet by a constant — no parser can fix that, so the
    // panel lets the listener slide it. Positive = the words are sung LATER than the sheet claims.
    // It is applied to the CLOCK, never to the parsed lines: IndexAt, LineProgress, SungWords, the glide
    // and the word stamps all read Now, so one expression moves every one of them, and nothing on disk
    // (or in the shared LyricLine list) is rewritten.
    private TimeSpan _offset;
    private const int StepMs = 200, MaxOffsetMs = 10000;
    private int _hotSync;                           // 1 = −, 2 = value, 3 = +
    private int _holdDir, _holdStart, _holdLast;    // press-and-hold auto-repeat
    private int _pressZone;                         // what the CURRENT press started on (see SyncHit)
    private bool _settling;                         // the glide has not arrived yet, so keep painting
    private Point _mouse = new(-1, -1);             // last known pointer, so hover follows a scrolling sheet

    // ---- the hand-off ----------------------------------------------------
    // When the sung line changes, the outgoing one does not drop out in a single frame: it keeps its weight
    // and eases down to its resting colour. (The INCOMING line needs nothing — it arrives with no words
    // filled, so the karaoke fill is its own entrance.)
    private int _shownCur = -1, _prevCur = -1, _handoffT0;
    private const int HandoffMs = 300;

    // ---- opening ---------------------------------------------------------
    // The sheet LANDS: the first paint puts the sung line straight at the focus point instead of gliding
    // there from the top of the song, which used to streak a screenful of unreadable lines past the eye.
    private bool _landed;
    private int _revealT0;

    // ---- held (paused) ---------------------------------------------------
    // A paused sheet exhales — it dims slightly and the accent bar cools — so a stopped song never looks
    // like an instrumental break.
    private double _held;
    private Tween? _holdTween;

    // ---- "wrong lyrics?" -------------------------------------------------
    // LRCLIB holds several sheets for most songs — the album cut, a single edit, a compilation — each timed
    // against ITS release. When the words are right but the clock is not, the answer is usually a different
    // sheet, not a different nudge, so the panel can show the list and let the listener take one.
    private bool _picking;
    private IReadOnlyList<LyricsCandidate>? _cands;   // null = still looking
    private int _pickHover = -1;
    private double _pickScroll;
    private bool _hoverMore;
    private TimeSpan _songLength;                   // the listener's own file, to judge the candidates by

    /// <summary>Whether the "other lyrics" button is offered at all — it needs the online lookup, which is
    /// the user's choice in Settings, so without it the button would only ever lead to an apology.</summary>
    public bool CanSearchVersions { get; set; } = true;

    /// <summary>Docked in the side card (not a popover): the card's own surface, the header row is the song line
    /// (the tab strip already says "Lyrics") with the picker + full-view buttons, and there is no × of its own.</summary>
    public bool Docked { get => _docked; set { _docked = value; BackColor = Surface; Invalidate(); } }
    private bool _docked;
    private Color Surface => _docked ? Theme.Bg : Theme.PanelBg;

    private double _scroll;                         // current pixel offset
    private double _userScroll;                     // manual wheel offset (decays back to follow mode)
    private int _lastUserScrollTick;
    private int _hover = -1;
    private bool _hoverClose;

    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };   // ~60 fps while visible

    private const int Pad = 18, HeaderH = 44, LineGap = 10, MinLineH = 26, FooterH = 30;
    /// <summary>A silence at least this long shows the three dots.</summary>
    private const double BeatSeconds = 4.0;

    /// <summary>The sync row only exists where it can do something — a sheet with real timings.</summary>
    private bool ShowSync => _synced && _lines.Count > 0;
    /// <summary>An untimed sheet gets the same strip, saying why nothing is following the song.</summary>
    private bool ShowFooter => _lines.Count > 0;
    /// <summary>Where the scrolling sheet ends (above the bottom strip, when there is one).</summary>
    private int SheetBottom => Height - (ShowFooter ? FooterH : 0);

    // 8 px of dead space on either side of the value: the middle target used to sit 2 px from both
    // steppers, and slipping onto it while stepping is how an adjustment gets thrown away.
    private Rectangle PlusRect => new(Width - Pad - 20, Height - FooterH + 5, 20, 20);
    private Rectangle ValueRect => new(Width - Pad - 90, Height - FooterH + 5, 62, 20);
    private Rectangle MinusRect => new(Width - Pad - 118, Height - FooterH + 5, 20, 20);

    private readonly Font _fHeader = Theme.DisplayFont(12.5f, FontStyle.Bold);
    private readonly Font _fSong = Theme.UiFont(9f);
    private readonly Font _fLine = Theme.UiFont(11.5f);
    private readonly Font _fLineOn = Theme.DisplayFont(13.5f, FontStyle.Bold);
    private readonly Font _fStatus = Theme.UiFont(10f);

    public LyricsPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Surface;

        _frame.Tick += (_, _) =>
        {
            // Press-and-hold on ± repeats, after a pause long enough that a single click stays a single step.
            if (_holdDir != 0 && ShowSync && Environment.TickCount - _holdStart > 380
                && Environment.TickCount - _holdLast > 70)
                Nudge(_holdDir);
            // Repaint only when something can still change. A paused sheet, or one with no timings, is a
            // static picture — redrawing it sixty times a second is pure heat.
            if (Visible && ((_playing && _synced) || _settling || _holdDir != 0 || (_synced && Math.Abs(_userScroll) > 0.5)))
                Invalidate();
        };
        VisibleChanged += (_, _) => { if (Visible) _frame.Start(); else _frame.Stop(); };

        MouseMove += (_, e) =>
        {
            _mouse = e.Location;
            if (_picking)
            {
                bool hm0 = ShowMore && MoreRect.Contains(e.Location);
                bool hc0 = CloseRect.Contains(e.Location);
                int ph = PickAt(e.Y);
                if (ph != _pickHover || hm0 != _hoverMore || hc0 != _hoverClose)
                {
                    _pickHover = ph; _hoverMore = hm0; _hoverClose = hc0;
                    Cursor = hc0 || hm0 || ph >= 0 ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
                return;
            }
            bool hm = ShowMore && MoreRect.Contains(e.Location);
            bool hx = ExpandRect.Contains(e.Location);
            bool hc = CloseRect.Contains(e.Location);
            int h = LineAt(e.Y);
            int hs = SyncHit(e.Location);
            // Dragging off the button you are holding stops the repeat, the way a real button does.
            if (_holdDir != 0 && hs != (_holdDir < 0 ? 1 : 3)) _holdDir = 0;
            if (h != _hover || hc != _hoverClose || hs != _hotSync || hm != _hoverMore || hx != _hoverExpand)
            {
                _hover = h; _hoverClose = hc; _hotSync = hs; _hoverMore = hm; _hoverExpand = hx;
                Cursor = hc || hm || hx || hs > 0 || (h >= 0 && _synced) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        };
        MouseLeave += (_, _) =>
        {
            _mouse = new Point(-1, -1);
            _hover = -1; _hoverClose = false; _hotSync = 0; _holdDir = 0; _pressZone = 0;
            _hoverMore = false; _hoverExpand = false; _pickHover = -1;
            Cursor = Cursors.Default; Invalidate();
        };
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _picking) return;
            // The stepper acts on PRESS, not on click, so holding it can repeat. Remember what the press
            // started on: a release somewhere else must not be treated as a click on that other thing.
            int hs = SyncHit(e.Location);
            _pressZone = hs;
            if (hs == 1 || hs == 3) { _holdDir = hs == 1 ? -1 : 1; _holdStart = Environment.TickCount; Nudge(_holdDir); }
        };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { _holdDir = 0; _pressZone = 0; } };
        // Double-click, not click: the number is a RESET, and a single click two pixels wide of the stepper
        // would silently throw away an adjustment the listener had just spent half a minute dialling in.
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && SyncHit(e.Location) == 2) SetOffsetMs(0, fromUser: true); };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            int started = _pressZone;
            if (CloseRect.Contains(e.Location)) { CloseRequested?.Invoke(); return; }
            if (ExpandRect.Contains(e.Location)) { ExpandRequested?.Invoke(); return; }
            if (ShowMore && MoreRect.Contains(e.Location)) { ToggleVersions(); return; }
            if (_picking)
            {
                int p = PickAt(e.Y);
                if (_cands is not null && p >= 0 && p < _cands.Count)
                {
                    var chosen = _cands[p];
                    _picking = false; _cands = null; _pickHover = -1; _pickScroll = 0;
                    Invalidate();
                    VersionChosen?.Invoke(chosen);
                }
                return;
            }
            if (started != 0 || SyncHit(e.Location) != 0) return;   // the press began on, or ended on, the sync row
            int i = LineAt(e.Y);
            // The seek must undo the nudge: the sheet is drawn against a shifted clock, so the moment the
            // user pointed at is the line's stamp plus the shift.
            if (_synced && i >= 0 && i < _lines.Count)
            {
                // Answer the hand: move our own clock to the clicked line NOW rather than waiting up to
                // 200 ms for the player's next report, so the line lights under the finger that picked it.
                if (_playing) { _reported = _lines[i].At + _offset; _since.Restart(); }
                SeekRequested?.Invoke(_lines[i].At + _offset);
            }
        };
        MouseWheel += (_, e) =>
        {
            if (_picking)
            {
                double max = Math.Max(0, (_cands?.Count ?? 0) * PickRowH - (SheetBottom - HeaderH) + 12);
                _pickScroll = Math.Clamp(_pickScroll - e.Delta * 0.5, 0, max);
                Invalidate();
                return;
            }
            if (_lines.Count == 0) return;
            // With timings the wheel is a temporary PEEK away from the followed line (it eases back below).
            // Without them it is the only scroll there is, so it stays where it is put - and it is clamped to
            // the sheet itself, so the words cannot be pushed off either end.
            _userScroll = _synced
                ? Math.Clamp(_userScroll - e.Delta * 0.6, -ContentHeight, ContentHeight)
                : Math.Clamp(_userScroll - e.Delta * 0.6, 0, MaxFreeScroll);
            _lastUserScrollTick = Environment.TickCount;
            Invalidate();
        };
    }

    private Rectangle CloseRect => _docked ? Rectangle.Empty : new(Width - Pad - 20, 12, 20, 20);
    /// <summary>The "other version" button, left of the ×. Only where it can do something: a sheet on
    /// screen, which is when the listener can tell it is the wrong one.</summary>
    private Rectangle MoreRect => new(Width - Pad - (_docked ? 20 : 46), 12, 20, 20);
    /// <summary>The "full view" button, left of the dots.</summary>
    private Rectangle ExpandRect => new(Width - Pad - (_docked ? (ShowMore ? 46 : 20) : 72), 12, 20, 20);
    private bool _hoverExpand;
    private bool ShowMore => CanSearchVersions && (_lines.Count > 0 || _picking);

    private const int PickRowH = 52;

    /// <summary>Replace the sheet. <paramref name="status"/> shows when there is nothing to display.</summary>
    public void SetLyrics(string songLine, IReadOnlyList<LyricLine> lines, bool synced, string status)
    {
        _songLine = songLine; _lines = lines; _synced = synced; _status = status;
        // A held button must not follow the song that replaced this one: playback rolls into the next
        // track on its own, and the remaining frames of the hold would be filed against the new song.
        _holdDir = 0; _pressZone = 0; _hotSync = 0;
        _shownCur = -1; _prevCur = -1; _handoffT0 = 0;
        _picking = false; _cands = null; _pickHover = -1; _pickScroll = 0;   // the list belonged to that song
        // Anchored to the moment the words arrive, NOT to the first paint: a panel that is not painted for
        // a while (a hidden window, a busy UI thread) must open already faded up, not start the fade late.
        _landed = false; _revealT0 = Environment.TickCount;
        _scroll = 0; _userScroll = 0; _measuredFor = -1;
        _reported = TimeSpan.Zero; _since.Restart();
        Invalidate();
    }

    /// <summary>Show or hide the list of other sheets. Opening it asks the host to go and look — the panel
    /// itself never touches the network.</summary>
    public void ToggleVersions()
    {
        _picking = !_picking;
        _pickHover = -1; _pickScroll = 0;
        if (_picking) { _cands = null; VersionsRequested?.Invoke(); }
        Invalidate();
    }

    /// <summary>The candidates found for this song (empty = nothing else exists). Ignored once the listener
    /// has closed the list again.</summary>
    /// <summary>How long the playing file actually is, so the list can say which sheet was written for a
    /// recording of the same length — the one that will fit.</summary>
    public void SetSongLength(TimeSpan len) { _songLength = len; if (_picking) Invalidate(); }

    public void SetCandidates(IReadOnlyList<LyricsCandidate> cands)
    {
        if (!_picking) return;
        _cands = cands;
        Invalidate();
    }

    private int PickAt(int mouseY)
    {
        if (!_picking || _cands is null || mouseY < HeaderH || mouseY >= SheetBottom) return -1;
        int i = (int)((mouseY - HeaderH - 6 + _pickScroll) / PickRowH);
        return i >= 0 && i < _cands.Count ? i : -1;
    }

    /// <summary>The saved sync nudge for this song, in milliseconds. Set by the host when the sheet loads;
    /// does NOT raise <see cref="OffsetChanged"/>, so restoring a stored value never re-saves it.</summary>
    public void SetOffsetMs(int ms) => SetOffsetMs(ms, fromUser: false);

    private void SetOffsetMs(int ms, bool fromUser)
    {
        ms = Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
        bool same = (int)Math.Round(_offset.TotalMilliseconds) == ms;
        _offset = TimeSpan.FromMilliseconds(ms);
        if (fromUser && !same) OffsetChanged?.Invoke(ms);
        Invalidate();
    }

    private void Nudge(int dir)
    {
        _holdLast = Environment.TickCount;
        SetOffsetMs((int)Math.Round(_offset.TotalMilliseconds) + dir * StepMs, fromUser: true);
    }

    /// <summary>Which part of the sync row the pointer is over: 1 = −, 2 = the value, 3 = +, 0 = none.</summary>
    private int SyncHit(Point p)
    {
        if (!ShowSync) return 0;
        if (MinusRect.Contains(p)) return 1;
        if (ValueRect.Contains(p)) return 2;
        if (PlusRect.Contains(p)) return 3;
        return 0;
    }

    /// <summary>The player's clock. Called a few times a second; the panel smooths between calls itself.</summary>
    public void SetPosition(TimeSpan pos, bool playing)
    {
        bool was = _playing;
        _reported = pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        _playing = playing;
        _since.Restart();
        if (was != playing)
        {
            // Ease into and out of the held state. Slow to stop (nothing should snap when the music does),
            // quick to come back, because the fill has to be bright again by the first sung word.
            double from = _held, to = playing ? 0 : 1;
            _holdTween?.Cancel();
            _holdTween = Anim.Run(playing ? 160 : 320,
                v => { _held = from + (to - from) * v; if (!IsDisposed) Invalidate(); },
                () => _holdTween = null,
                playing ? Easings.OutCubic : Easings.OutQuad);
        }
        if (!playing) Invalidate();   // a paused sheet still needs one repaint to settle
    }

    /// <summary>The interpolated position the panel draws against. Interpolation is CAPPED: if the player
    /// stops reporting (the track ended, or the engine stalled) the sheet freezes where it was instead of
    /// scrolling away on a clock nobody is driving any more.</summary>
    private static readonly TimeSpan MaxDrift = TimeSpan.FromSeconds(2);
    private TimeSpan Now
    {
        get
        {
            var pos = _reported;
            if (_playing)
            {
                var since = _since.Elapsed;
                pos += since > MaxDrift ? MaxDrift : since;
            }
            return pos - _offset;   // the user's sync nudge, applied once, for everything downstream
        }
    }

    // ---- layout: words are measured once per width, so paint stays cheap ----
    /// <summary>A laid-out line. The WRAP is decided once with the bold (active) font so a line never
    /// re-wraps when it becomes current, but each word carries BOTH widths so the x positions can be packed
    /// per font — measuring with one font and drawing with a narrower one leaves ugly gaps.</summary>
    private sealed class Word { public string W = ""; public int VisualLine; public float Bold, Reg; }
    private sealed class Row
    {
        public int Top, Height, LineH;
        public List<Word> Words = new();
        /// <summary>A timestamped line with no words and a long wait after it: an instrumental stretch,
        /// drawn as three filling dots rather than as nothing at all.</summary>
        public bool Beat;
    }
    private Row[] _rows = Array.Empty<Row>();
    private int _measuredFor = -1;
    private double ContentHeight => _rows.Length == 0 ? 0 : _rows[^1].Top + _rows[^1].Height;
    /// <summary>How far an untimed sheet may be scrolled: the last line stops at the bottom of the view.</summary>
    private double MaxFreeScroll => Math.Max(0, ContentHeight - (SheetBottom - HeaderH) + 12);

    /// <summary>Wrap every line into word rectangles. Word boxes are what make the karaoke fill possible —
    /// a plain text draw gives no way to say "this much of the line has been sung".</summary>
    private void Measure()
    {
        if (_measuredFor == Width && _rows.Length == _lines.Count) return;
        _rows = new Row[_lines.Count];
        int maxW = Math.Max(60, Width - 2 * Pad), y = 0;
        using var g = CreateGraphics();

        for (int i = 0; i < _lines.Count; i++)
        {
            var row = new Row { Top = y };
            string text = _lines[i].Text;
            if (text.Length == 0)
            {
                // How long the silence lasts decides whether it earns the dots: a beat between two lines of
                // a verse is punctuation, a sixteen-second break is a place the listener needs told about.
                double gap = i + 1 < _lines.Count ? (_lines[i + 1].At - _lines[i].At).TotalSeconds : 0;
                row.Beat = gap >= BeatSeconds;
                row.Height = row.Beat ? MinLineH : MinLineH / 2;
                _rows[i] = row; y += row.Height + LineGap; continue;
            }

            row.LineH = TextRenderer.MeasureText(g, "Xg", _fLineOn).Height;
            float spaceBold = Measure1(g, " ", _fLineOn);
            float x = 0;
            int visual = 0;
            foreach (string w in SplitWords(text))
            {
                float bold = Measure1(g, w, _fLineOn);
                if (x > 0 && x + bold > maxW) { x = 0; visual++; }
                row.Words.Add(new Word { W = w, VisualLine = visual, Bold = bold, Reg = Measure1(g, w, _fLine) });
                x += bold + spaceBold;
            }
            row.Height = Math.Max(MinLineH, (visual + 1) * row.LineH);
            _rows[i] = row;
            y += row.Height + LineGap;
        }
        _spaceBold = Measure1(g, " ", _fLineOn);
        _spaceReg = Measure1(g, " ", _fLine);
        _measuredFor = Width;
    }

    private static IEnumerable<string> SplitWords(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static float Measure1(Graphics g, string s, Font f) =>
        TextRenderer.MeasureText(g, s, f, new Size(9999, 99), TextFormatFlags.NoPadding).Width;

    /// <summary>Word rectangles packed for the font actually being drawn, keeping the shared wrap points.</summary>
    private List<RectangleF> Laid(Row row, bool bold)
    {
        var outp = new List<RectangleF>(row.Words.Count);
        float x = 0, space = bold ? _spaceBold : _spaceReg;
        int line = 0;
        foreach (var w in row.Words)
        {
            if (w.VisualLine != line) { line = w.VisualLine; x = 0; }
            float wide = bold ? w.Bold : w.Reg;
            outp.Add(new RectangleF(x, line * row.LineH, wide, row.LineH));
            x += wide + space;
        }
        return outp;
    }

    private float _spaceBold = 4, _spaceReg = 4;

    private int LineAt(int mouseY)
    {
        if (_lines.Count == 0 || mouseY < HeaderH || mouseY >= SheetBottom) return -1;
        Measure();
        double y = mouseY - HeaderH + _scroll + _userScroll;
        for (int i = 0; i < _rows.Length; i++)
            if (y >= _rows[i].Top && y < _rows[i].Top + _rows[i].Height + LineGap) return i;
        return -1;
    }

    /// <summary>How far through the CURRENT line we are, 0..1. Uses real word timings when the source had
    /// them, otherwise spreads the line evenly over the gap to the next one.</summary>
    private double LineProgress(int i, TimeSpan now)
    {
        if (i < 0 || i >= _lines.Count) return 0;
        var line = _lines[i];
        TimeSpan end = i + 1 < _lines.Count ? _lines[i + 1].At : line.At + TimeSpan.FromSeconds(5);
        double span = (end - line.At).TotalSeconds;
        if (span <= 0.01) return 1;
        return Math.Clamp((now - line.At).TotalSeconds / span, 0, 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(Surface);

        if (_picking)
        {
            DrawPicker(g);
            DrawHeader(g);
            return;
        }

        if (_lines.Count == 0)
        {
            DrawHeader(g);
            TextRenderer.DrawText(g, _status, _fStatus, new Rectangle(Pad, HeaderH, Width - 2 * Pad, Height - HeaderH),
                Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        Measure();
        // The sheet slides under a still pointer, so the hovered row has to be recomputed as we draw it —
        // otherwise the accent stays on whichever line happened to be there when the mouse last moved.
        if (_mouse.X >= 0 && _hotSync == 0) _hover = LineAt(_mouse.Y);
        var now = Now;
        int cur = _synced ? LyricsLookup.IndexAt(_lines, now) : -1;

        if (cur != _shownCur) { _prevCur = _shownCur; _shownCur = cur; _handoffT0 = Environment.TickCount; }
        double ho = !Anim.MotionEnabled || _handoffT0 == 0
            ? 1 : Math.Clamp((Environment.TickCount - _handoffT0) / (double)HandoffMs, 0, 1);
        double reveal = !Anim.MotionEnabled || _revealT0 == 0
            ? 1 : Math.Clamp((Environment.TickCount - _revealT0) / 260.0, 0, 1);
        double exhale = 1 - 0.15 * _held;

        // ---- scroll: an exponential glide, recomputed every frame ----
        // Frame-rate independent (the 1-exp form), so it lands the same whether we paint at 60 or at 20 fps,
        // and it never "arrives" abruptly the way a fixed-duration tween between lines does.
        // Ease back to the followed line after a peek - but ONLY when there is a line to follow. With no
        // timings the sheet has no "home" to return to, and this used to slide the words back to the first
        // line a few seconds after every scroll.
        if (_synced && Environment.TickCount - _lastUserScrollTick > 4000) _userScroll *= 0.86;
        // A timestamped line with no words is an instrumental beat. Nothing is sung, so nothing lights up —
        // but the sheet must not sit staring at a blank: aim at the next line that actually has words, so the
        // listener is already reading what comes back in.
        int aim = cur;
        while (aim >= 0 && aim < _rows.Length && _rows[aim].Words.Count == 0) aim++;
        // Past the last word (the outro): aim BACK at the final line that has words, so the song's last
        // line stays where the eye is instead of the sheet emptying out under the header.
        if (aim >= _rows.Length)
        {
            aim = cur;
            while (aim > 0 && _rows[aim].Words.Count == 0) aim--;
        }
        // A long break keeps the dots at the focus point instead of pre-scrolling: the sheet only sets off
        // for the next line about a second before it is sung, so the words arrive already in place.
        if (_synced && cur >= 0 && cur < _rows.Length && _rows[cur].Beat && cur + 1 < _lines.Count
            && (_lines[cur + 1].At - now).TotalSeconds > 0.9) aim = cur;

        double dtF = Math.Min(0.05, _frameTime.Elapsed.TotalSeconds);
        if (_synced && _lines.Count > 0)
        {
            // Before the first line the sheet aims at line 1 exactly where it will sit when it lights, so
            // the opening hand-off is a pure change of colour with no scroll at all.
            int t = cur >= 0 ? aim : 0;
            double target = _rows[t].Top;
            if (cur >= 0 && t == cur && cur + 1 < _rows.Length)
                target += (_rows[cur + 1].Top - _rows[cur].Top) * LineProgress(cur, now);
            target -= (SheetBottom - HeaderH) * 0.40;
            if (!_landed) { _scroll = target; _landed = true; }     // open ON the song, not above it
            else _scroll += (target - _scroll) * (1 - Math.Exp(-dtF / 0.16));
            _settling = Math.Abs(target - _scroll) > 0.5;
        }
        else { _landed = true; _settling = false; }
        _settling |= reveal < 1 || ho < 1;
        _frameTime.Restart();

        // ---- the sheet ----
        g.SetClip(new Rectangle(0, HeaderH, Width, SheetBottom - HeaderH));
        double top = _scroll + _userScroll;
        double prog = cur >= 0 ? LineProgress(cur, now) : 0;

        // Where the eye rests. Lines dim by their PIXEL distance from it, not by how many lines away they
        // are, so brightness follows the glide continuously instead of the whole sheet re-colouring in one
        // frame every time the sung line changes.
        double focusY = HeaderH + (SheetBottom - HeaderH) * 0.40 + MinLineH / 2.0;

        // Before the first word: three dots at the focus, filling as the intro runs out.
        if (_synced && cur < 0 && _rows.Length > 0 && _lines[0].At.TotalSeconds >= BeatSeconds)
        {
            int y0 = (int)Math.Round(HeaderH + _rows[0].Top - top);
            DrawBeat(g, Pad, y0 - 22, now.TotalSeconds / _lines[0].At.TotalSeconds, now.TotalSeconds, reveal * exhale);
        }

        for (int i = 0; i < _rows.Length; i++)
        {
            int y = (int)Math.Round(HeaderH + _rows[i].Top - top);
            if (y + _rows[i].Height < HeaderH) continue;
            if (y > SheetBottom) break;
            if (_rows[i].Words.Count == 0)
            {
                if (_rows[i].Beat && _synced && i == cur && i + 1 < _lines.Count)
                {
                    double span = (_lines[i + 1].At - _lines[i].At).TotalSeconds;
                    DrawBeat(g, Pad, y + _rows[i].Height / 2, span <= 0 ? 1 : (now - _lines[i].At).TotalSeconds / span,
                             now.TotalSeconds, reveal * exhale);
                }
                continue;
            }

            bool isNow = _synced && i == cur;
            bool isPast = _synced && i < cur;
            bool isPrev = _synced && i == _prevCur && ho < 1 && !isNow;
            double d = Math.Clamp(Math.Abs(y + _rows[i].Height / 2.0 - focusY) / 190.0, 0, 1);
            double bright = _synced ? 0.66 - 0.42 * Math.Pow(d, 1.15) : 0.62;
            // The CURRENT line's not-yet-sung words must be clearly darker than the sung ones, or the
            // karaoke fill is invisible — but not darker than the line BELOW, or the eye is pulled off the
            // row being sung.
            Color dim = isNow
                ? Theme.Blend(Surface, Theme.TextCol, 0.46 * exhale)
                : Theme.Blend(Surface, isPast ? Theme.Subtle : Theme.TextCol, bright * (isPast ? 0.85 : 1) * exhale);

            // Hover: a slab behind the line, the way macOS Music does it. Tinting the WORDS accent-teal was
            // an app-ism — text that changes hue on hover reads as a link, not as a place to click.
            if (i == _hover && _synced)
            {
                var slab = new Rectangle(Pad - 10, y - 5, Width - 2 * Pad + 20, _rows[i].Height + 10);
                using var hp = Theme.RoundedRect(slab, 8);
                using var hb = new SolidBrush(Color.FromArgb((int)(18 * reveal), Theme.TextCol));
                g.FillPath(hb, hp);
                if (!isNow) dim = Theme.Blend(dim, Theme.TextCol, 0.30);
            }
            dim = Fade(dim, reveal);

            if (isPrev)
            {
                // The line just sung keeps its weight and walks its colour down, so the hand-off reads as a
                // pass rather than as two things changing in the same 16 ms frame.
                DrawWords(g, _rows[i], y, true, Fade(Theme.Blend(Theme.TextCol, dim, Easings.OutQuad(ho)), reveal));
            }
            else if (!isNow) DrawWords(g, _rows[i], y, false, dim);
            else
            {
                // The sung line lights up as a WHOLE, fading in over the hand-off. A word-by-word fill was a
                // guess at where the voice is — the sources give LINE times, not word times — and it read as
                // jitter rather than as karaoke. Held (paused) it steps back from full white.
                Color full = Fade(Theme.Blend(Theme.TextCol, Theme.Blend(Surface, Theme.TextCol, 0.72), _held), reveal);
                DrawWords(g, _rows[i], y, true, Theme.Blend(dim, full, Easings.OutCubic(ho)));
                // the accent bar grows with the line, so even a long line shows movement
                int track = Math.Max(10, _rows[i].Height - 4);
                using (var t = new SolidBrush(Fade(Theme.Blend(Surface, Theme.Accent, 0.28), reveal)))
                    g.FillRectangle(t, Pad - 9, y + 2, 3, track);                       // the whole line…
                using (var b = new SolidBrush(Fade(Theme.Blend(Theme.Accent, Theme.Blend(Theme.Accent, Surface, 0.45), _held), reveal)))
                    g.FillRectangle(b, Pad - 9, y + 2, 3, (int)(track * prog));         // …filled so far
            }
        }
        g.ResetClip();

        // top/bottom fades so lines melt into the popover edges
        using (var lg = new LinearGradientBrush(new Rectangle(0, HeaderH, Width, 28), Surface, Color.FromArgb(0, Surface), LinearGradientMode.Vertical))
            g.FillRectangle(lg, 0, HeaderH, Width, 28);
        using (var lg = new LinearGradientBrush(new Rectangle(0, SheetBottom - 28, Width, 28), Color.FromArgb(0, Surface), Surface, LinearGradientMode.Vertical))
            g.FillRectangle(lg, 0, SheetBottom - 28, Width, 28);

        // The chrome goes on LAST, over the sheet. TextRenderer paints through GDI and ignores the Graphics
        // clip, so a lyric line straddling the top or the bottom of the sheet band draws straight across the
        // song title or the sync row — the clip that should have stopped it is silently not honoured.
        DrawHeader(g);
        if (ShowSync) DrawSyncBar(g);
        else if (ShowFooter) DrawUntimedNote(g);
    }

    /// <summary>The list of other sheets: what each one is, how long its release runs, and whether it is
    /// timed. The length is the thing to read — a sheet written against a recording the same length as
    /// yours is almost always the one that fits.</summary>
    private void DrawPicker(Graphics g)
    {
        var band = new Rectangle(0, HeaderH, Width, SheetBottom - HeaderH);
        if (_cands is null || _cands.Count == 0)
        {
            TextRenderer.DrawText(g, _cands is null ? Loc.T("Looking for other lyrics…") : Loc.T("No other lyrics found."),
                _fStatus, band, Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        g.SetClip(band);
        for (int i = 0; i < _cands.Count; i++)
        {
            int y = (int)(HeaderH + 6 + i * PickRowH - _pickScroll);
            if (y + PickRowH < HeaderH) continue;
            if (y > SheetBottom) break;
            var c = _cands[i];

            if (i == _pickHover)
            {
                using var hp = Theme.RoundedRect(new Rectangle(Pad - 8, y, Width - 2 * Pad + 16, PickRowH - 6), 8);
                using var hb = new SolidBrush(Color.FromArgb(20, Theme.TextCol));
                g.FillPath(hb, hp);
            }

            // The length is the thing to read: a sheet timed against a recording as long as yours fits;
            // one written for a different cut is out by however much the two differ. Green says "this one".
            string len = ((int)c.Duration.TotalMinutes) + ":" + (c.Duration.Seconds).ToString("00");
            double off = _songLength > TimeSpan.Zero && c.Duration > TimeSpan.Zero
                ? Math.Abs((c.Duration - _songLength).TotalSeconds) : -1;
            Color lenCol = off < 0 ? Theme.Subtle : off <= 2 ? Theme.Accent : off <= 5 ? Theme.Subtle : Theme.Faint;
            TextRenderer.DrawText(g, len, _fStatus, new Rectangle(Width - Pad - 58, y + 6, 58, 18),
                lenCol, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, c.Album.Length > 0 ? c.Album : c.Artist, _fSong,
                new Rectangle(Pad, y + 4, Width - 2 * Pad - 64, 20), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, c.Synced ? Loc.T("{0} timed lines", c.Lines) : Loc.T("words only, no timing"),
                _fStatus, new Rectangle(Pad, y + 24, Width - 2 * Pad, 16),
                c.Synced ? Theme.Subtle : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        g.ResetClip();
    }

    /// <summary>The "full view" glyph: two arrow heads pointing out of the corner — the same mark the stage
    /// turns inward to come back.</summary>
    private void DrawExpand(Graphics g, Rectangle r, bool hot)
    {
        if (hot)
        {
            using var hp = Theme.RoundedRect(r, 6);
            using var hb = new SolidBrush(Theme.Blend(Surface, Theme.TextCol, 0.14f));
            g.FillPath(hb, hp);
        }
        using var pen = new Pen(hot ? Theme.TextCol : Theme.Subtle, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        g.DrawLine(pen, cx + 5, cy - 5, cx + 1, cy - 1); g.DrawLine(pen, cx + 1, cy - 5, cx + 5, cy - 5); g.DrawLine(pen, cx + 5, cy - 1, cx + 5, cy - 5);
        g.DrawLine(pen, cx - 5, cy + 5, cx - 1, cy + 1); g.DrawLine(pen, cx - 1, cy + 5, cx - 5, cy + 5); g.DrawLine(pen, cx - 5, cy + 1, cx - 5, cy + 5);
    }

    /// <summary>The "other version" glyph: three dots, the universal "there is more here". In the list it
    /// becomes a back arrow, because the same button is the way out.</summary>
    private void DrawMore(Graphics g, Rectangle r, bool hot, bool back)
    {
        if (hot)
        {
            using var hp = Theme.RoundedRect(r, 6);
            using var hb = new SolidBrush(Theme.Blend(Surface, Theme.TextCol, 0.14f));
            g.FillPath(hb, hp);
        }
        var c = hot ? Theme.TextCol : Theme.Subtle;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        if (back)
        {
            using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, cx + 3, cy - 4, cx - 3, cy);
            g.DrawLine(pen, cx - 3, cy, cx + 3, cy + 4);
            return;
        }
        using var b = new SolidBrush(c);
        for (int k = -1; k <= 1; k++) g.FillEllipse(b, cx + k * 5 - 1.3f, cy - 1.3f, 2.6f, 2.6f);
    }

    /// <summary>Everything on the sheet rises out of the background together when it first appears, so a
    /// popover that opens mid-song does not slam a full page of text down at once.</summary>
    private Color Fade(Color c, double reveal) => reveal >= 1 ? c : Theme.Blend(Surface, c, reveal);

    /// <summary>The three dots that stand in for the words during an intro or an instrumental break: they
    /// fill left to right as the silence runs out, and breathe while they wait. Drawn with GDI+ ellipses,
    /// which DO honour the clip region (unlike TextRenderer), so they can never spill into the chrome.</summary>
    private void DrawBeat(Graphics g, int x, int cy, double prog, double t, double alpha)
    {
        prog = Math.Clamp(prog, 0, 1);
        Color off = Theme.Blend(Surface, Theme.TextCol, 0.30 * alpha);
        Color on = Theme.Blend(Surface, Theme.TextCol, alpha);
        for (int k = 0; k < 3; k++)
        {
            double f = Math.Clamp(prog * 3 - k, 0, 1);
            double breathe = Anim.MotionEnabled ? 0.55 * Math.Sin(2 * Math.PI * t / 1.9 - k * 0.7) : 0;
            float r = (float)(3.2 + breathe);
            using var b = new SolidBrush(Theme.Blend(off, on, f));
            g.FillEllipse(b, x + k * 12 - r, cy - r, r * 2, r * 2);
        }
    }

    /// <summary>Why an untimed sheet just sits there. Without this the panel looks broken rather than
    /// honest — the words are right, nobody wrote down when they are sung.</summary>
    private void DrawUntimedNote(Graphics g)
    {
        int y0 = Height - FooterH;
        PaintBand(g, new Rectangle(0, y0, Width, FooterH));
        using (var pen = new Pen(Theme.HairLine)) g.DrawLine(pen, Pad, y0, Width - Pad, y0);
        TextRenderer.DrawText(g, Loc.T("No timing — the sheet can't follow along."), _fStatus,
            new Rectangle(Pad, y0, Width - 2 * Pad, FooterH), Theme.Subtle,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Re-lay the window background over one band, so whatever GDI spilled there is covered.</summary>
    private void PaintBand(Graphics g, Rectangle band)
    {
        g.SetClip(band);
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint))
        {
            using var b = new SolidBrush(Surface);
            g.FillRectangle(b, band);
        }
        g.ResetClip();
    }

    private void DrawHeader(Graphics g)
    {
        PaintBand(g, new Rectangle(0, 0, Width, HeaderH));
        int titleW = Width - 2 * Pad - (ShowMore ? 82 : 56);
        if (_docked)
        {
            // the tab strip names the panel; this row is the song (or the picker's title), the buttons stay
            TextRenderer.DrawText(g, _picking ? Loc.T("Other lyrics") : _songLine.Length > 0 ? _songLine : Loc.T("Lyrics"), _fSong,
                new Rectangle(Pad, 0, Width - 2 * Pad - (ShowMore ? 56 : 30), HeaderH), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            TextRenderer.DrawText(g, _picking ? Loc.T("Other lyrics") : Loc.T("Lyrics"), _fHeader,
                new Rectangle(Pad, 6, titleW, 20), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (_songLine.Length > 0)
                TextRenderer.DrawText(g, _songLine, _fSong, new Rectangle(Pad, 24, titleW, 16),
                    Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawX(g, CloseRect, _hoverClose);
        }
        if (ShowMore) DrawMore(g, MoreRect, _hoverMore, _picking);
        DrawExpand(g, ExpandRect, _hoverExpand);
        using var pen = new Pen(Theme.HairLine);
        g.DrawLine(pen, Pad, HeaderH - 1, Width - Pad, HeaderH - 1);
    }

    /// <summary>The sync row: a label, a stepper, and the current shift. It sits under the bottom fade, where
    /// the sheet has already dissolved, so it reads as a control rather than as part of the lyrics — and the
    /// number sits BETWEEN the two buttons, directly under the eye that just pressed one.</summary>
    private void DrawSyncBar(Graphics g)
    {
        int y0 = Height - FooterH;
        PaintBand(g, new Rectangle(0, y0, Width, FooterH));
        using (var pen = new Pen(Theme.HairLine)) g.DrawLine(pen, Pad, y0, Width - Pad, y0);

        TextRenderer.DrawText(g, Loc.T("Sync"), _fStatus, new Rectangle(Pad, y0 + 6, 140, 18),
            Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int ms = (int)Math.Round(_offset.TotalMilliseconds);
        // A real minus sign, not a hyphen: it sits two centimetres from the drawn "−" button and a thin
        // stubby hyphen next to it reads as a different character.
        string num = (ms > 0 ? "+" : ms < 0 ? "−" : "") + (Math.Abs(ms) / 1000.0).ToString("0.0");
        // Non-zero is drawn in the accent colour: a single press changes the number AND lights it up, so the
        // control answers immediately even though one step barely moves the sheet.
        if (_hotSync == 2)
        {
            using var path = Theme.RoundedRect(ValueRect, 6);
            using var back = new SolidBrush(Theme.Blend(Surface, Theme.TextCol, 0.14f));
            g.FillPath(back, path);
        }
        TextRenderer.DrawText(g, Loc.T("{0} s", num), _fStatus, ValueRect,
            ms == 0 ? Theme.Subtle : Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        DrawStep(g, MinusRect, plus: false, hot: _hotSync == 1);
        DrawStep(g, PlusRect, plus: true, hot: _hotSync == 3);
    }

    private void DrawStep(Graphics g, Rectangle r, bool plus, bool hot)
    {
        if (hot)
        {
            using var path = Theme.RoundedRect(r, 6);
            using var back = new SolidBrush(Theme.Blend(Surface, Theme.TextCol, 0.14f));
            g.FillPath(back, path);
        }
        using var pen = new Pen(hot ? Theme.TextCol : Theme.Subtle, 1.7f);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, m = 5;
        g.DrawLine(pen, cx - m, cy, cx + m, cy);
        if (plus) g.DrawLine(pen, cx, cy - m, cx, cy + m);
    }

    private readonly Stopwatch _frameTime = Stopwatch.StartNew();

    private void DrawWords(Graphics g, Row row, int y, bool bold, Color col)
    {
        var rects = Laid(row, bold);
        var font = bold ? _fLineOn : _fLine;
        for (int i = 0; i < row.Words.Count; i++)
            TextRenderer.DrawText(g, row.Words[i].W, font,
                new Point(Pad + (int)rects[i].X, y + (int)rects[i].Y), col, TextFormatFlags.NoPadding);
    }

    private static void DrawX(Graphics g, Rectangle r, bool hot)
    {
        using var p = new Pen(hot ? Theme.TextCol : Theme.Subtle, 1.6f);
        int m = 6;
        g.DrawLine(p, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
        g.DrawLine(p, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _holdTween?.Cancel();
            _frame.Stop(); _frame.Dispose();
            _fHeader.Dispose(); _fSong.Dispose(); _fLine.Dispose(); _fLineOn.Dispose(); _fStatus.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The popover that hosts <see cref="LyricsPanel"/>, anchored under the bar's lyrics button.</summary>
internal sealed class LyricsFlyout : FlyoutForm
{
    public event Action<TimeSpan>? SeekRequested;
    public event Action<int>? OffsetChanged;
    public event Action? VersionsRequested;
    public event Action<LyricsCandidate>? VersionChosen;
    public event Action? ExpandRequested;

    private const int Wide = 380, Tall = 460;
    private readonly LyricsPanel _panel;

    public LyricsFlyout()
    {
        GlassEnabled = Glass.PopupsEnabled;
        ClientSize = new Size(Wide, Tall);
        _panel = new LyricsPanel { Dock = DockStyle.Fill };
        _panel.CloseRequested += Close;
        _panel.SeekRequested += t => SeekRequested?.Invoke(t);
        _panel.OffsetChanged += ms => OffsetChanged?.Invoke(ms);
        _panel.VersionsRequested += () => VersionsRequested?.Invoke();
        _panel.VersionChosen += c => VersionChosen?.Invoke(c);
        _panel.ExpandRequested += () => ExpandRequested?.Invoke();
        Controls.Add(_panel);
    }

    public void SetLyrics(string songLine, IReadOnlyList<LyricLine> lines, bool synced, string status)
        => _panel.SetLyrics(songLine, lines, synced, status);

    public void SetPosition(TimeSpan pos, bool playing) => _panel.SetPosition(pos, playing);

    public void SetOffsetMs(int ms) => _panel.SetOffsetMs(ms);

    public void SetCandidates(IReadOnlyList<LyricsCandidate> cands) => _panel.SetCandidates(cands);
    public void ToggleVersions() => _panel.ToggleVersions();
    public void SetSongLength(TimeSpan len) => _panel.SetSongLength(len);
    public bool CanSearchVersions { set => _panel.CanSearchVersions = value; }
}
