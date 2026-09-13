using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace iPodCommander;

/// <summary>
/// A Cover-Flow album browser: the centre cover faces the viewer flat (and sits a touch closer); covers to each
/// side recede in true perspective (a foreshortened trapezoid) with a glossy mirrored reflection, and the deck
/// glides between covers. Navigate with the mouse wheel, arrow keys, a drag (with a flick), or by clicking a side
/// cover; click the centre cover (or Enter) to activate it; Esc closes.
///
/// Rendering: every cover is a per-column projective warp of its source pixels, done here in software because
/// GDI+ has no perspective transform. The one or two covers crossing the centre are warped LIVE every frame at
/// their exact angle and sub-pixel position (the columns run in parallel; a millisecond or two), so the rotation
/// is continuous - no angle steps, no stand-in cards. Resting covers (the flat centre, the full-angle sides) are
/// warped once, cached as sprites and blitted 1:1. Edges and the rounded corners are anti-aliased analytically
/// (exact pixel coverage), and the texture is filtered through a horizontal mip chain plus a vertical box, so
/// nothing shimmers or steps however far a side cover is foreshortened.
/// </summary>
internal sealed class CoverFlowView : Control
{
    public sealed record Item(Bitmap Cover, string Title, string Subtitle, object? Tag);

    /// <summary>The square, full-bleed source size the host should feed (ArtworkService.LoadSquare): the largest
    /// bake base is 420 px, so a 420 px source is never upscaled by more than the centre cover's pop.</summary>
    internal const int SourcePx = 420;

    private readonly List<Item> _items = new();
    private float _pos;        // current fractional centre index (animates toward _target)
    private int _target;       // index we're gliding to
    private Tween? _tw;

    // ---- caches ----
    private readonly Dictionary<long, Bitmap> _sprites = new();    // resting sprites: key = (index, cover version, side)
    private readonly Dictionary<long, int> _spriteUse = new();      // key -> the frame it was last drawn in (LRU for the byte budget)
    private readonly List<long> _scratchKeys = new();               // reused so the eviction passes allocate nothing
    private long _spriteBytes;
    private int _frameNo;
    private const long SpriteBudget = 96L * 1024 * 1024;
    private const int KeepReach = 4;                                // covers past the visible edge whose sprites/pixels are kept
    private readonly Dictionary<long, SrcMips> _src = new();        // (index, version) -> the cover pixels + their x-mip chain
    private readonly Dictionary<int, int> _coverVer = new();        // per-index cover version (bumped when a real cover streams in)
    private int _bakedCoverH = -1;                                  // base size the current sprites were baked at (rebake on resize)
    private int _centreH;                                           // the popped centre cover's size (= base * Pop)
    private float _maxAngle, _flatPhase;                            // per-paint geometry the bakes need
    private int _themeRev = -1;                                     // Theme.Revision the backdrop + reflections were built for
    private Bitmap? _live;                                          // the per-frame warp target for the covers crossing the centre
    private Bitmap? _bg;                                            // cached backdrop, re-rendered on resize / theme change only
    private Bitmap? _vignette;                                      // cached edge-darkening overlay
    // Chrome fonts + the close-button pen draw on EVERY paint; Theme.UiFont/DisplayFont allocate a fresh GDI Font
    // per call, so they are hoisted here (font families are static-readonly). Disposed in Dispose().
    private readonly Font _fCentreTitle = Theme.DisplayFont(13f, FontStyle.Bold);
    private readonly Font _fCentreSub = Theme.UiFont(10f);
    private readonly Font _fMode = Theme.UiFont(9f, FontStyle.Bold);
    private readonly Font _fNpChip = Theme.UiFont(8.75f, FontStyle.Bold);
    private readonly Pen _closePen = new(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };  // colour reassigned per frame
    private float _lastPaintPos;                                    // _pos at the previous paint (bench: per-frame speed)
    private int _visRange = 6;                                      // visible covers each side of centre (from the last paint)
    private float _intro = 1f;                                      // open/close zoom+fade (1 = fully shown)
    private Tween? _introTween;
    private readonly List<(int index, RectangleF rect)> _hit = new();
    private Rectangle _closeRect;
    private bool _closeHover;
    // drag-to-scrub + flick
    private bool _mouseDown, _dragging;
    private int _downX;
    private float _downPos, _stepPx = 60f;     // _stepPx = horizontal px between covers (cached from paint)
    private readonly long[] _trailT = new long[6];   // the last drag samples (time, position) -> release velocity
    private readonly float[] _trailP = new float[6];
    private int _trailN;
    // currently-playing album (set by the host) -> marker on its cover + a "Now Playing" chip
    private object? _playingTag;
    private Rectangle _npChip;
    private bool _npChipHover;
    // Songs / Albums / Artists segmented toggle (top-centre) - the host rebuilds the deck on change.
    public enum BrowseMode { Songs, Albums, Artists }
    private BrowseMode _mode = BrowseMode.Albums;
    private static readonly string[] ModeLabels = { "Songs", "Albums", "Artists" };
    private readonly Rectangle[] _modeRects = { Rectangle.Empty, Rectangle.Empty, Rectangle.Empty };
    private int _modeHover = -1;

    public event Action<Item>? Activated;
    public event Action? CloseRequested;
    public event Action<BrowseMode>? ModeChanged;

    /// <summary>Which kind of cover the deck shows. Set by the host; the toggle reflects it.</summary>
    public BrowseMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; Invalidate(); } }

    /// <summary>The Tag of the album currently playing (set by the host); marks its cover + enables the chip.</summary>
    public object? PlayingTag { get => _playingTag; set { if (Equals(_playingTag, value)) return; _playingTag = value; Invalidate(); } }

    private const float MaxAngleDeg = 70f;   // steep side-cover angle (classic Cover Flow look)
    private const float Pop = 1.06f;         // the centre cover sits a touch closer: 6% bigger than the sides, eased in as it arrives
    private const float ViewerDist = 1.85f;  // perspective strength: viewer distance as a multiple of the cover size
    private const float FrameAlpha = 0.13f;  // the faint 1 px inner frame every cover tile in the app wears

    // ---- bench hooks (harness only; nothing runs when Trace is null) ----
    internal static Action<string>? Trace;   // one CSV line per paint: tick,pos,fast,paintMs,live,0,0,sprites
    private int _statLive;
    internal int BakeCount; internal double BakeMs;   // bench: resting-sprite bake totals
    internal int QueueDepth => 0;                     // (there is no background baker any more)

    public CoverFlowView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        TabStop = true;
    }

    public int CurrentIndex => Math.Clamp((int)Math.Round(_pos), 0, Math.Max(0, _items.Count - 1));
    public bool Settled => Math.Abs(_pos - _target) < 0.01f;

    /// <summary>Diagnostics: cached resting sprites + the cover pixel data (with mips) held right now.</summary>
    internal (int Sprites, long SpriteBytes, int Pixels, long PixelBytes) CacheStats()
    {
        long pb = 0;
        foreach (var m in _src.Values) pb += (long)m.Data.Length * 4;
        return (_sprites.Count, _spriteBytes, _src.Count, pb);
    }

    /// <summary>Harness only: park the deck at a fractional position (a frame mid-crossing) for a render.</summary>
    internal void PreviewPos(float pos)
    {
        _tw?.Cancel();
        _pos = Math.Clamp(pos, 0, Math.Max(0, _items.Count - 1));
        _target = (int)Math.Round(_pos);
        Invalidate();
    }

    public void SetItems(IEnumerable<Item> items, int start = 0)
    {
        _tw?.Cancel();
        ClearCaches();
        _items.Clear();
        _items.AddRange(items);
        _target = Math.Clamp(start, 0, Math.Max(0, _items.Count - 1));
        _pos = _target;
        Invalidate();
    }

    /// <summary>Swap in a real cover for an item (covers stream in after the view is shown). The bitmap is
    /// owned by the caller (e.g. ArtworkService's cache) - never disposed here; only our warp caches are.</summary>
    public void SetCover(int index, Bitmap cover)
    {
        if (index < 0 || index >= _items.Count || cover is null) return;
        _items[index] = _items[index] with { Cover = cover };
        EvictIndex(index);   // drop this item's pixels + sprites so they re-warp with the real art
        Invalidate();
    }

    // ---- navigation ----

    public void MoveTo(int index) => Glide(index, force: false);
    public new void Move(int delta) => MoveTo(_target + delta);   // (hides Control.Move, the event)

    private void Glide(int index, bool force)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        if (!force && index == _target) return;
        _target = index;
        _tw?.Cancel();
        if (!Anim.MotionEnabled) { _pos = _target; Invalidate(); return; }
        float from = _pos, to = _target, dist = Math.Abs(to - from);
        if (dist < 0.0005f) { _pos = to; Invalidate(); return; }
        // Glide time scales gently with distance (snappy single steps, a longer glide for big jumps). A single
        // step lands with a soft spring (3% overshoot, then settles); a longer glide just decelerates smoothly.
        // Continuing from the current position keeps rapid flicks fluid.
        double dur = Math.Clamp(230 + 90 * Math.Sqrt(dist), 230, 560);
        Func<double, double> ease = dist <= 1.25f ? Spring : Easings.OutQuint;
        _tw = Anim.Run(dur, v => { _pos = from + (float)((to - from) * v); if (!IsDisposed) InvalidateDeck(); },
            () => { if (!IsDisposed) InvalidateDeck(); }, ease);
    }

    /// <summary>OutBack with a gentle constant: overshoots the landing by about 3% and eases back.</summary>
    private static double Spring(double t) { const double c1 = 0.9, c3 = c1 + 1; double s = t - 1; return 1 + c3 * s * s * s + c1 * s * s; }

    private Rectangle _band;   // the covers + reflections + centre text, from the last paint
    /// <summary>A glide changes only the deck band: repainting just that skips the top chrome and the floor below
    /// the text - a third of the pixels on a big window, every frame of every flick.</summary>
    private void InvalidateDeck() { if (_band.Height > 0 && _intro >= 0.999f) Invalidate(_band); else Invalidate(); }

    /// <summary>Play the open animation: the deck zooms up and fades in.</summary>
    public void AnimateIn()
    {
        _introTween?.Cancel();
        if (!Anim.MotionEnabled) { _intro = 1f; Invalidate(); return; }
        _intro = 0f;
        _introTween = Anim.Run(300, v => { _intro = (float)v; if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    /// <summary>Play the close animation (zoom down + fade out), then run <paramref name="done"/>.</summary>
    public void AnimateOut(Action done)
    {
        _introTween?.Cancel();
        if (!Anim.MotionEnabled) { done(); return; }
        float from = _intro;
        _introTween = Anim.Run(170, v => { _intro = from * (1f - (float)v); if (!IsDisposed) Invalidate(); }, done, Easings.OutCubic);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter or Keys.Escape || base.IsInputKey(keyData);

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Move(e.Delta > 0 ? -1 : 1); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Move(-1); break;
            case Keys.Right: Move(1); break;
            case Keys.Home: MoveTo(0); break;
            case Keys.End: MoveTo(_items.Count - 1); break;
            case Keys.Enter: ActivateCentre(); break;
            case Keys.Escape: CloseRequested?.Invoke(); break;
        }
    }

    // Type-to-jump: press a letter/number to jump to the next album whose title starts with it.
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        char c = char.ToLowerInvariant(e.KeyChar);
        if (!char.IsLetterOrDigit(c) || _items.Count == 0) return;
        for (int k = 1; k <= _items.Count; k++)
        {
            int i = (CurrentIndex + k) % _items.Count;
            string t = (_items[i].Title ?? "").TrimStart();
            if (t.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(4);
            if (t.Length > 0 && char.ToLowerInvariant(t[0]) == c) { MoveTo(i); break; }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_closeRect.Contains(e.Location)) { CloseRequested?.Invoke(); return; }
        for (int i = 0; i < 3; i++)
            if (_modeRects[i].Contains(e.Location)) { if ((int)_mode != i) { _mode = (BrowseMode)i; Invalidate(); ModeChanged?.Invoke(_mode); } return; }
        if (_playingTag is not null && _npChip.Contains(e.Location)) { JumpToPlaying(); return; }
        _mouseDown = true; _dragging = false; _downX = e.X; _downPos = _pos; _trailN = 0;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown)
        {
            if (!_dragging && Math.Abs(e.X - _downX) > 4) _dragging = true;
            if (_dragging)
            {
                _tw?.Cancel();   // a grab mid-glide takes over
                _pos = Math.Clamp(_downPos - (e.X - _downX) / Math.Max(1f, _stepPx), 0, Math.Max(0, _items.Count - 1));
                TrailPush(_pos);
                Invalidate();
            }
            return;
        }
        bool ch = _closeRect.Contains(e.Location);
        if (ch != _closeHover) { _closeHover = ch; Invalidate(_closeRect); }
        bool nh = _playingTag is not null && _npChip.Contains(e.Location);
        if (nh != _npChipHover) { _npChipHover = nh; Invalidate(_npChip); }
        int mh = -1; for (int i = 0; i < 3; i++) if (_modeRects[i].Contains(e.Location)) { mh = i; break; }
        if (mh != _modeHover) { _modeHover = mh; Invalidate(); }
        Cursor = (ch || nh || mh >= 0 || HitTest(e.Location) >= 0) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_mouseDown) return;
        _mouseDown = false;
        if (_dragging)
        {
            _dragging = false;
            // A flick carries on: the release velocity throws the deck a few covers further (clamped), then it
            // settles on a cover; a slow drag just snaps to the nearest one.
            float fling = Math.Clamp(TrailVelocity() * 0.20f, -6f, 6f);
            Glide((int)Math.Round(_pos + fling), force: true);
            return;
        }
        int hit = HitTest(e.Location);                                               // a click (no drag)
        if (hit < 0) return;
        if (hit == CurrentIndex && Settled) ActivateCentre(); else MoveTo(hit);
    }

    private void TrailPush(float pos)
    {
        long now = Environment.TickCount64;
        if (_trailN == _trailT.Length)
        {
            Array.Copy(_trailT, 1, _trailT, 0, _trailN - 1);
            Array.Copy(_trailP, 1, _trailP, 0, _trailN - 1);
            _trailN--;
        }
        _trailT[_trailN] = now; _trailP[_trailN] = pos; _trailN++;
    }

    /// <summary>The drag's release velocity in covers per second, from the samples of its last ~130 ms; zero when
    /// the pointer paused before letting go.</summary>
    private float TrailVelocity()
    {
        if (_trailN < 2) return 0f;
        long now = Environment.TickCount64;
        if (now - _trailT[_trailN - 1] > 90) return 0f;
        int k = _trailN - 1;
        while (k > 0 && now - _trailT[k - 1] <= 130) k--;
        long dt = _trailT[_trailN - 1] - _trailT[k];
        if (dt < 8) return 0f;
        return (_trailP[_trailN - 1] - _trailP[k]) * 1000f / dt;
    }

    private void JumpToPlaying()
    {
        if (_playingTag is null) return;
        for (int i = 0; i < _items.Count; i++) if (Equals(_items[i].Tag, _playingTag)) { MoveTo(i); return; }
    }

    private void ActivateCentre() { int i = CurrentIndex; if (i >= 0 && i < _items.Count) Activated?.Invoke(_items[i]); }
    private int HitTest(Point p) { for (int k = _hit.Count - 1; k >= 0; k--) if (_hit[k].rect.Contains(p)) return _hit[k].index; return -1; }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        _frameNo++;   // LRU stamp for the sprite budget: anything drawn this frame is the last to be evicted
        var swPaint = Trace is null ? null : System.Diagnostics.Stopwatch.StartNew();
        _statLive = 0;
        var g = e.Graphics;
        _hit.Clear();
        bool fast = Math.Abs(_pos - _lastPaintPos) > 0.25f;   // (bench only)
        _lastPaintPos = _pos;

        // A palette switch invalidates everything baked in theme colours: the backdrop, the vignette and the
        // sprites (their reflections fade toward the floor colour).
        if (_themeRev != Theme.Revision)
        {
            _themeRev = Theme.Revision;
            _bg?.Dispose(); _bg = null; _vignette?.Dispose(); _vignette = null;
            ClearSprites();
        }

        // Backdrop: a dark vertical gradient, cached as a bitmap (re-rendered only when the size changes)
        // and blitted 1:1 each frame - far cheaper than gradient-filling the whole control every paint.
        if (_bg is null || _bg.Width != Width || _bg.Height != Height)
        {
            _bg?.Dispose();
            _bg = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
            using var bgg = Graphics.FromImage(_bg);
            bgg.SmoothingMode = SmoothingMode.AntiAlias;
            // Backdrop in the app's own theme colour (a touch lighter at top, darker "floor" at the bottom).
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, _bg.Width, _bg.Height), Theme.Blend(Theme.Bg, Color.White, 0.04), Theme.Blend(Theme.Bg, Color.Black, 0.22), 90f))
                bgg.FillRectangle(br, 0, 0, _bg.Width, _bg.Height);
            // Soft center spotlight behind the covers for depth/focus.
            using (var gp = new GraphicsPath())
            {
                var er = new RectangleF(_bg.Width * 0.06f, -_bg.Height * 0.25f, _bg.Width * 0.88f, _bg.Height * 1.05f);
                gp.AddEllipse(er);
                using var pgb = new PathGradientBrush(gp)
                { CenterColor = Theme.Blend(Theme.Bg, Color.White, 0.10), SurroundColors = new[] { Color.FromArgb(0, Theme.Bg) }, CenterPoint = new PointF(_bg.Width / 2f, _bg.Height * 0.40f) };
                bgg.FillPath(pgb, gp);
            }
        }
        var prevCM = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;   // _bg is opaque -> skip the per-pixel alpha blend on the big full-screen blit
        g.DrawImageUnscaled(_bg, 0, 0);
        g.CompositingMode = prevCM;                        // covers + vignette need SourceOver

        if (_items.Count == 0) { DrawCloseButton(g); return; }

        // Fast per-frame compositing: sprites are rendered at final size, so blit 1:1 (no resampling).
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.SmoothingMode = SmoothingMode.None;

        int H = Height;
        // Covers bake at this BASE size. It tracks the window HEIGHT (covers fill ~46% of it) but is also held
        // under a fraction of the WIDTH so a wide / full-screen window grows the covers (bigger deck). The
        // open/close zoom is a runtime scaled blit (not a smaller bake); a base-size change (resize / maximize)
        // rebakes the cache so covers re-fit.
        int baseH = Math.Clamp((int)Math.Min(H * 0.46f, Width * 0.34f), 130, 420);
        if (baseH != _bakedCoverH)
        {
            ClearSprites();
            _bakedCoverH = baseH;
            _centreH = (int)Math.Round(baseH * Pop);
            _live?.Dispose();
            _live = new Bitmap(_centreH + 2, _centreH + _centreH / 2 + 2, PixelFormat.Format32bppPArgb);
        }
        int centreH = _centreH;
        float introScale = 0.84f + 0.16f * Math.Clamp(_intro, 0f, 1f); // open/close zoom (applied as a scaled blit below)
        float cx = Width / 2f, centreY = H * 0.42f;
        _maxAngle = (float)(MaxAngleDeg * Math.PI / 180);
        // The settled centre cover is blitted from its cached flat sprite, which is baked at the sub-pixel phase
        // of its resting place so the last live frame and the cached one line up exactly (no half-pixel snap).
        float flatLeft = cx - centreH / 2f, ph = flatLeft - MathF.Floor(flatLeft);
        if (Math.Abs(ph - _flatPhase) > 0.001f) { _flatPhase = ph; DropSprites(k => (int)(k & 3) == 1); }
        // The centre cover is flat and popped forward; the side covers recede as an overlapping fan. side1 =
        // first side cover's centre; sideStep = spacing between side covers. Both are tuned so the first side
        // cover slips slightly UNDER the centre cover (no backdrop gap line), and side covers overlap enough
        // that their foreshortened (sloped) tops don't leave a dark backdrop wedge between neighbours.
        float projFull = baseH * (float)Math.Cos(_maxAngle);
        float side1 = centreH * 0.5f + projFull / 2f - baseH * 0.04f;
        float sideStep = projFull * 0.52f;
        _stepPx = sideStep;                                         // for drag-to-scrub
        // Fan out enough covers to reach the screen edges (capped for perf), so a wide / full-screen window
        // shows a full-width deck rather than a short fan stranded in the middle.
        int range = Math.Clamp((int)Math.Ceiling((Width / 2f - side1) / sideStep) + 2, 5, 8);
        _visRange = range;

        int lo = Math.Max(0, (int)Math.Floor(_pos) - range);
        int hi = Math.Min(_items.Count - 1, (int)Math.Ceiling(_pos) + range);
        _band = new Rectangle(0, (int)(centreY - centreH / 2f) - 12, Width, (int)(centreH * 1.5f + centreH * 0.42f + 70) + 12);   // covers + reflections + centre text: what a glide repaints
        // Draw farthest-from-centre first (back) and the centre last (front): each cover overlaps the one
        // further out, the centre on top. Two cursors walking inward from both ends reproduce that exact
        // farthest-first order with zero per-frame allocation.
        int dlo = lo, dhi = hi;
        while (dlo <= dhi)
        {
            int i = Math.Abs(dlo - _pos) >= Math.Abs(dhi - _pos) ? dlo++ : dhi--;
            DrawCover(g, i, cx, centreY, baseH, centreH, side1, sideStep, introScale);
        }

        // Edge vignette: darken the far side covers toward the screen edges for depth (drawn over them).
        // Cached as a transparent overlay - and blitted as only its two non-empty EDGE STRIPS (the wide middle is
        // fully transparent, so a full-width alpha blit just churned ~half the pixels for nothing).
        int vw = (int)(Width * 0.24f);
        if (_vignette is null || _vignette.Width != Width || _vignette.Height != H)
        {
            _vignette?.Dispose();
            _vignette = new Bitmap(Math.Max(1, Width), Math.Max(1, H), PixelFormat.Format32bppPArgb);
            using var vg = Graphics.FromImage(_vignette);
            Color edge = Theme.Blend(Theme.Bg, Color.Black, 0.6);
            // NOTE: the gradient-brush rect is 1px WIDER than the fill on each end: a LinearGradientBrush renders
            // its very first column at the WRAPPED (end) colour - here that put a hard dark line where the right
            // vignette starts. Pushing the brush edges outside the fill region hides that buggy column.
            using (var lv = new LinearGradientBrush(new Rectangle(-1, 0, vw + 2, H), Color.FromArgb(165, edge), Color.FromArgb(0, edge), 0f))
                vg.FillRectangle(lv, 0, 0, vw, H);
            using (var rv = new LinearGradientBrush(new Rectangle(Width - vw - 1, 0, vw + 2, H), Color.FromArgb(0, edge), Color.FromArgb(165, edge), 0f))
                vg.FillRectangle(rv, Width - vw, 0, vw, H);
        }
        g.DrawImage(_vignette, new Rectangle(0, 0, vw, H), 0, 0, vw, H, GraphicsUnit.Pixel);                       // left strip
        g.DrawImage(_vignette, new Rectangle(Width - vw, 0, vw, H), Width - vw, 0, vw, H, GraphicsUnit.Pixel);     // right strip

        // Bound the caches: drop the sprites + pixels of covers that scrolled well out of view, then hold the
        // sprite bytes under the budget by evicting the least recently drawn (never one drawn this frame).
        // Runs every paint, so it allocates nothing (scratch list + plain loops).
        if (_sprites.Count > 60) DropSprites(k => { int idx = (int)(k >> 4); return idx < lo - KeepReach || idx > hi + KeepReach; });
        while (_spriteBytes > SpriteBudget && _sprites.Count > 0)
        {
            int oldest = int.MaxValue;
            foreach (var kv in _spriteUse) if (kv.Value < oldest) oldest = kv.Value;
            if (oldest == int.MaxValue || oldest == _frameNo) break;
            DropSprites(k => _spriteUse.TryGetValue(k, out int u) && u == oldest);
        }
        if (_src.Count > 28)
        {
            _scratchKeys.Clear();
            foreach (var k in _src.Keys) { int idx = (int)(k >> 2); if (idx < lo - 2 || idx > hi + 2) _scratchKeys.Add(k); }
            foreach (var k in _scratchKeys) _src.Remove(k);
        }

        DrawCentreText(g, centreY, centreH);
        DrawNowPlayingChip(g);
        DrawModeSwitch(g);
        DrawCloseButton(g);

        // At rest, warp the resting sprites a little beyond the visible deck now, so the newcomers of the next
        // flick are ready before they slide in.
        if (Settled) Prebake(lo, hi);
        if (swPaint is not null)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            Trace!($"{Environment.TickCount64},{_pos.ToString("F3", ic)},{(fast ? 1 : 0)},{swPaint.Elapsed.TotalMilliseconds.ToString("F2", ic)},{_statLive},0,0,{_sprites.Count}");
        }
    }

    private void Prebake(int lo, int hi)
    {
        int centre = (int)Math.Round(_pos), r = _visRange + 3;
        for (int i = centre - r; i <= centre + r; i++)
        {
            if (i < 0 || i >= _items.Count || (i >= lo && i <= hi)) continue;   // the visible ones were baked by the paint
            GetSprite(i, i < centre ? -1 : 1);
        }
    }

    /// <summary>A Songs / Albums / Artists segmented toggle centred at the top - clicking a segment raises
    /// <see cref="ModeChanged"/> so the host rebuilds the deck.</summary>
    private void DrawModeSwitch(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var f = _fMode;
        const int padX = 17, h = 30;
        int[] w = new int[3]; int total = 0;
        for (int i = 0; i < 3; i++) { w[i] = TextRenderer.MeasureText(g, Loc.T(ModeLabels[i]), f).Width + padX * 2; total += w[i]; }
        int x = (Width - total) / 2, y = 14;

        using (var b = new SolidBrush(Color.FromArgb((int)(40 * _intro), Color.White)))
        using (var cp = Theme.RoundedRect(new Rectangle(x, y, total, h), h / 2f)) g.FillPath(b, cp);

        int cxx = x;
        for (int i = 0; i < 3; i++)
        {
            var seg = new Rectangle(cxx, y, w[i], h);
            _modeRects[i] = seg;
            bool active = (int)_mode == i;
            var inner = Rectangle.Inflate(seg, -3, -3);
            if (active)
                using (var ab = new SolidBrush(Color.FromArgb((int)(235 * _intro), Theme.Accent)))
                using (var ap = Theme.RoundedRect(inner, inner.Height / 2f)) g.FillPath(ab, ap);
            else if (_modeHover == i)
                using (var hb = new SolidBrush(Color.FromArgb((int)(45 * _intro), Color.White)))
                using (var hp = Theme.RoundedRect(inner, inner.Height / 2f)) g.FillPath(hb, hp);
            Color tcol = active ? Theme.OnAccent : Color.White;
            TextRenderer.DrawText(g, Loc.T(ModeLabels[i]), f, seg, Color.FromArgb((int)((active ? 255 : 205) * _intro), tcol),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            cxx += w[i];
        }
    }

    /// <summary>A "Now Playing" pill (top-left) shown when an album in the deck is the one playing; click it
    /// (or it's just a cue) to fly the deck back to that album.</summary>
    private void DrawNowPlayingChip(Graphics g)
    {
        _npChip = Rectangle.Empty;
        if (_playingTag is null) return;
        bool present = false;
        for (int i = 0; i < _items.Count; i++) if (Equals(_items[i].Tag, _playingTag)) { present = true; break; }
        if (!present) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var f = _fNpChip;
        string txt = Loc.T("Now Playing");
        int tw = TextRenderer.MeasureText(g, txt, f).Width;
        _npChip = new Rectangle(16, 14, 16 + 16 + 8 + tw + 14, 30);
        using (var b = new SolidBrush(Color.FromArgb((int)((_npChipHover ? 64 : 38) * _intro), Color.White)))
        using (var cp = Theme.RoundedRect(_npChip, 15)) g.FillPath(b, cp);
        // three little accent equaliser bars
        using (var ab = new SolidBrush(Color.FromArgb((int)(255 * _intro), Theme.AccentBright)))
        {
            float bx = _npChip.X + 16, by = _npChip.Y + _npChip.Height / 2f + 6;
            float[] hs = { 8, 13, 6 };
            for (int k = 0; k < 3; k++) g.FillRectangle(ab, bx + k * 4.5f, by - hs[k], 2.6f, hs[k]);
        }
        TextRenderer.DrawText(g, txt, f, new Rectangle(_npChip.X + 38, _npChip.Y, tw + 10, _npChip.Height), Color.FromArgb((int)(255 * _intro), Color.White),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawCover(Graphics g, int i, float cx, float centreY, int baseH, int centreH, float side1, float sideStep, float scale)
    {
        float d = i - _pos;
        float a = Math.Abs(d);
        int s = d < 0 ? -1 : 1;
        // |d|<=1: interpolate the centre cover out to the first side slot; beyond: recede by sideStep.
        float o = a <= 1f ? d * side1 : s * (side1 + (a - 1f) * sideStep);
        float Xc = cx + o;

        if (a >= 0.002f && a < 0.999f)
        {
            // Crossing the centre: warped live at its exact angle, size and sub-pixel position, every frame.
            float theta = _maxAngle * a;
            int ch = (int)Math.Round(baseH * (1f + (Pop - 1f) * (1f - a)));   // eases up to the popped centre size
            float pw = ch * (float)Math.Cos(theta);
            float left = Xc - pw / 2f;
            int leftI = (int)MathF.Floor(left);
            float phase = left - leftI;
            int bufW = Math.Clamp((int)MathF.Ceiling(phase + pw), 1, _live!.Width), reflH = ch / 2;
            WarpInto(_live, bufW, ch, reflH, GetSrc(i), theta, nearRight: d > 0, phase);
            _statLive++;
            float top = centreY - ch * scale / 2f;
            var srcRect = new Rectangle(0, 0, bufW, ch + reflH);
            if (scale >= 0.999f)
                g.DrawImage(_live, new Rectangle(leftI, (int)Math.Round(top), bufW, ch + reflH), srcRect, GraphicsUnit.Pixel);
            else
            {
                var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Bilinear;   // open/close zoom frames: smooth scale
                g.DrawImage(_live, new RectangleF(Xc - bufW * scale / 2f, top, bufW * scale, (ch + reflH) * scale), srcRect, GraphicsUnit.Pixel);
                g.InterpolationMode = im;
            }
            _hit.Add((i, new RectangleF(leftI, top, pw * scale, ch * scale)));
            return;
        }

        // Resting: the flat centre or a full-angle side cover, from the sprite cache (warped once, blitted 1:1).
        bool flat = a < 0.002f;
        var sprite = GetSprite(i, flat ? 0 : s);
        int coverH = flat ? centreH : baseH;
        float dw = sprite.Width * scale, dh = sprite.Height * scale;
        float top2 = centreY - coverH * scale / 2f;
        float left2 = flat ? MathF.Floor(cx - centreH / 2f) : Xc - dw / 2f;   // the flat sprite carries its own sub-pixel phase
        _hit.Add((i, new RectangleF(left2, top2, dw, coverH * scale)));
        if (scale >= 0.999f)
        {
            // A pure side cover shows only its OUTER strip (one sideStep wide): the neighbour nearer the centre is
            // drawn over its inner half. Blitting just that strip halves the per-frame cost of the whole fan.
            int strip = (int)Math.Ceiling(sideStep) + 2;
            if (a > 1.5f && strip < sprite.Width)
            {
                int sx = s < 0 ? 0 : sprite.Width - strip;
                g.DrawImage(sprite, new Rectangle((int)Math.Round(left2) + sx, (int)Math.Round(top2), strip, sprite.Height), new Rectangle(sx, 0, strip, sprite.Height), GraphicsUnit.Pixel);
            }
            else g.DrawImageUnscaled(sprite, (int)Math.Round(left2), (int)Math.Round(top2)); // resting: 1:1, no resample
        }
        else
        {
            var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(sprite, flat ? cx - dw / 2f : left2, top2, dw, dh);                                    // open/close zoom frames: smooth scale
            g.InterpolationMode = im;
        }
    }

    // ---- the resting-sprite cache ----

    // Sprite cache key: index | cover-version | side (all non-overlapping bit fields). side field: 0 = left, 1 = flat centre, 2 = right.
    private static long SpriteKey(int index, int ver, int side) => ((long)index << 4) | ((long)(ver & 3) << 2) | (uint)(side + 1);
    private int Ver(int index) => _coverVer.TryGetValue(index, out var v) ? v : 0;

    /// <summary>The resting sprite of a cover (side 0 = flat at the popped centre size, +/-1 = the full side angle),
    /// warped on first use and cached.</summary>
    private Bitmap GetSprite(int index, int side)
    {
        long key = SpriteKey(index, Ver(index), side);
        if (_sprites.TryGetValue(key, out var hit)) { _spriteUse[key] = _frameNo; return hit; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int ch = side == 0 ? _centreH : _bakedCoverH;
        float theta = side == 0 ? 0f : _maxAngle, phase = side == 0 ? _flatPhase : 0f;
        float pw = ch * (float)Math.Cos(theta);
        int w = Math.Max(1, (int)MathF.Ceiling(pw + phase)), reflH = ch / 2;
        var bmp = new Bitmap(w, ch + reflH, PixelFormat.Format32bppPArgb);
        WarpInto(bmp, w, ch, reflH, GetSrc(index), theta, nearRight: side > 0, phase);
        _sprites[key] = bmp;
        _spriteBytes += (long)bmp.Width * bmp.Height * 4;
        _spriteUse[key] = _frameNo;
        BakeCount++; BakeMs += sw.Elapsed.TotalMilliseconds;
        return bmp;
    }

    private void DropSprites(Func<long, bool> which)
    {
        _scratchKeys.Clear();
        foreach (var k in _sprites.Keys) if (which(k)) _scratchKeys.Add(k);
        foreach (var k in _scratchKeys)
        {
            var b = _sprites[k];
            _spriteBytes -= (long)b.Width * b.Height * 4;
            b.Dispose();
            _sprites.Remove(k);
            _spriteUse.Remove(k);
        }
    }

    /// <summary>A cover's pixels with their horizontal mip chain (built once per index + version), for the warp.</summary>
    private SrcMips GetSrc(int index)
    {
        long k = ((long)index << 2) | (uint)(Ver(index) & 3);
        if (_src.TryGetValue(k, out var m)) return m;
        int w, h; int[] arr;
        try
        {
            var bmp = _items[index].Cover;
            w = bmp.Width; h = bmp.Height;
            arr = new int[w * h];
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, arr, 0, w * h);   // 32bpp -> stride == w*4, no row padding
            bmp.UnlockBits(d);
        }
        catch
        {
            // A cover bitmap gone bad under us (disposed by its owner's cache): a plain dark tile rather than a crash
            // mid-paint; the real art is re-requested through SetCover whenever it streams in again.
            w = h = 2; arr = new int[4];
            var t = Theme.Blend(Theme.PanelBg, Color.White, 0.06);
            Array.Fill(arr, (255 << 24) | (t.R << 16) | (t.G << 8) | t.B);
        }
        m = new SrcMips(arr, w, h);
        _src[k] = m;
        return m;
    }

    private void EvictIndex(int index)
    {
        _scratchKeys.Clear();
        foreach (var k in _src.Keys) if ((int)(k >> 2) == index) _scratchKeys.Add(k);
        foreach (var k in _scratchKeys) _src.Remove(k);
        DropSprites(k => (int)(k >> 4) == index);
        _coverVer[index] = Ver(index) + 1;
    }

    private void ClearSprites() => DropSprites(_ => true);

    private void ClearCaches()
    {
        ClearSprites();
        _src.Clear();
    }

    // ---- the warp ----

    /// <summary>A cover's ARGB pixels plus a chain of HORIZONTALLY halved copies (widths w, w/2, w/4 ... at the full
    /// height), all in one array. A foreshortened cover compresses the source far more across than down, so the
    /// warp picks the level whose columns match its horizontal step (blending two levels) and box-filters the
    /// remaining vertical minification directly - anisotropic filtering without a full ripmap's memory.</summary>
    private sealed class SrcMips
    {
        public readonly int[] Data;
        public readonly int[] Off, W;
        public readonly int H, W0, Levels;

        public SrcMips(int[] px, int w, int h)
        {
            H = h; W0 = w;
            int levels = 1, ww = w;
            while (ww > 8 && levels < 8) { ww = (ww + 1) / 2; levels++; }
            Levels = levels; W = new int[levels]; Off = new int[levels];
            int total = 0; ww = w;
            for (int k = 0; k < levels; k++) { W[k] = ww; Off[k] = total; total += ww * h; ww = (ww + 1) / 2; }
            Data = new int[total];
            Array.Copy(px, 0, Data, 0, w * h);
            for (int k = 1; k < levels; k++)
            {
                int sw = W[k - 1], dw = W[k], so = Off[k - 1], dof = Off[k];
                for (int y = 0; y < h; y++)
                {
                    int srow = so + y * sw, drow = dof + y * dw;
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Math.Min(2 * x, sw - 1), x1 = Math.Min(2 * x + 1, sw - 1);
                        int p = Data[srow + x0], q = Data[srow + x1];
                        Data[drow + x] = ((((p >> 24) & 0xFF) + ((q >> 24) & 0xFF) + 1) >> 1) << 24
                                       | ((((p >> 16) & 0xFF) + ((q >> 16) & 0xFF) + 1) >> 1) << 16
                                       | ((((p >> 8) & 0xFF) + ((q >> 8) & 0xFF) + 1) >> 1) << 8
                                       | (((p & 0xFF) + (q & 0xFF) + 1) >> 1);
                    }
                }
            }
        }
    }

    private struct WarpJob
    {
        public IntPtr Dst; public int Stride, BufW, CoverH, ReflH;
        public SrcMips Src;
        public float PwF, Phase, Q;
        public bool NearRight;
        public float CornerR, FloorR, FloorG, FloorB;
    }

    /// <summary>Render one cover - a perspective-warped, rounded-cornered, framed tile at <paramref name="theta"/>
    /// (0 = flat) with its faded mirror reflection beneath - into the top-left <paramref name="bufW"/> x
    /// (<paramref name="coverH"/> + <paramref name="reflH"/>) pixels of <paramref name="dst"/> (premultiplied
    /// ARGB, every pixel of that rectangle written). The tile spans [phase, phase + coverH*cos(theta)) across the
    /// buffer, so <paramref name="phase"/> places it at a sub-pixel x. Columns run in parallel.</summary>
    private static void WarpInto(Bitmap dst, int bufW, int coverH, int reflH, SrcMips src, float theta, bool nearRight, float phase)
    {
        float sinT = (float)Math.Sin(theta), dv = coverH * ViewerDist;
        Color floor = Theme.Blend(Theme.Bg, Color.Black, 0.22);   // the backdrop's bottom colour (see OnPaint's gradient)
        var job = new WarpJob
        {
            BufW = bufW, CoverH = coverH, ReflH = reflH, Src = src,
            PwF = Math.Max(1f, coverH * (float)Math.Cos(theta)), Phase = phase,
            Q = (dv - coverH / 2f * sinT) / (dv + coverH / 2f * sinT),   // far-edge height fraction
            NearRight = nearRight, CornerR = Theme.TileFrac,
            FloorR = floor.R, FloorG = floor.G, FloorB = floor.B,
        };
        var data = dst.LockBits(new Rectangle(0, 0, bufW, coverH + reflH), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        job.Dst = data.Scan0; job.Stride = data.Stride;
        try
        {
            if (bufW >= 64)
                Parallel.ForEach(Partitioner.Create(0, bufW, Math.Max(8, bufW / 24)), r => WarpColumns(job, r.Item1, r.Item2));
            else WarpColumns(job, 0, bufW);
        }
        finally { dst.UnlockBits(data); }
    }

    private static unsafe void WarpColumns(WarpJob j, int x0, int x1)
    {
        byte* dst = (byte*)j.Dst;
        int stride = j.Stride, coverH = j.CoverH, reflH = j.ReflH, h = j.Src.H, maxL = j.Src.Levels - 1;
        float pwF = j.PwF, q = j.Q, rr = j.CornerR;
        fixed (int* basePtr = j.Src.Data)
        {
            for (int ox = x0; ox < x1; ox++)
            {
                byte* col = dst + ox * 4;
                // Horizontal coverage of this column by the tile's span [phase, phase + pwF): the two edge
                // columns are partial (that is the anti-aliasing of the vertical edges + the sub-pixel placement).
                float xs0 = Math.Max(ox, j.Phase), xs1 = Math.Min(ox + 1f, j.Phase + pwF);
                float cxv = xs1 - xs0;
                if (cxv <= 0.0005f) { for (int y = 0; y < coverH + reflH; y++) *(int*)(col + y * stride) = 0; continue; }
                if (cxv > 1f) cxv = 1f;
                float xs = (xs0 + xs1) * 0.5f - j.Phase;                 // sample at the covered part's centre
                float s = j.NearRight ? 1f - xs / pwF : xs / pwF;         // 0 = near (tall) edge .. 1 = far edge
                if (s < 0f) s = 0f; else if (s > 1f) s = 1f;
                float den = (1f - s) + s * q;
                float hgt = coverH * den;                                  // this column's foreshortened height
                float yTop = (coverH - hgt) * 0.5f, yBot = yTop + hgt;
                float u = s * q / den;                                     // perspective-correct source position (near -> far)
                float xfrac = j.NearRight ? 1f - u : u;                    // source x, left -> right
                float sxs = j.Src.W0 * (q / (den * den)) / pwF;            // source px per output px across (level 0)
                float sys = h / hgt;                                       // ... and down
                // Horizontal filtering: the mip level whose step is <= 1 px, blended with the next (trilinear
                // across levels, so the sharpness never seams between columns).
                float lvl = sxs > 1f ? MathF.Log2(sxs) : 0f;
                if (lvl > maxL) lvl = maxL;
                int l0 = (int)lvl;
                int wl = (int)((lvl - l0) * 256f + 0.5f);
                if (wl < 8) wl = 0; else if (wl > 248 && l0 < maxL) { l0++; wl = 0; }
                int l1 = Math.Min(l0 + 1, maxL);
                bool two = wl > 0 && l1 != l0;
                int w0 = j.Src.W[l0], w1 = j.Src.W[l1];
                int* p0 = basePtr + j.Src.Off[l0], p1 = basePtr + j.Src.Off[l1];
                float fx0 = xfrac * w0 - 0.5f; int ix0 = (int)MathF.Floor(fx0); int wx0 = (int)((fx0 - ix0) * 256f + 0.5f);
                int xa0 = Math.Clamp(ix0, 0, w0 - 1), xb0 = Math.Clamp(ix0 + 1, 0, w0 - 1);
                float fx1 = xfrac * w1 - 0.5f; int ix1 = (int)MathF.Floor(fx1); int wx1 = (int)((fx1 - ix1) * 256f + 0.5f);
                int xa1 = Math.Clamp(ix1, 0, w1 - 1), xb1 = Math.Clamp(ix1 + 1, 0, w1 - 1);
                // Vertical filtering: a box of n bilinear taps across the output pixel when the source is minified.
                int n = sys <= 1.25f ? 1 : Math.Min(4, (int)MathF.Ceiling(sys - 0.25f));
                // Rounded corners: the arc's signed distance in the source's unit square, scaled per axis into
                // output pixels so the anti-aliasing ramp is one screen pixel wide whatever the foreshortening.
                float du = Math.Min(xfrac, 1f - xfrac);
                bool xCorner = du < rr;
                float gx = sxs / j.Src.W0, gy = 1f / hgt;                  // unit-square distance per output px, across / down
                float dxEdge = Math.Min(xs, pwF - xs);                     // px to the nearer vertical edge (the inner frame)

                int y0 = Math.Max(0, (int)yTop), y1 = Math.Min(coverH - 1, (int)MathF.Ceiling(yBot) - 1);
                for (int y = 0; y < y0; y++) *(int*)(col + y * stride) = 0;
                for (int y = y1 + 1; y < coverH; y++) *(int*)(col + y * stride) = 0;
                for (int oy = y0; oy <= y1; oy++)
                {
                    int* op = (int*)(col + oy * stride);
                    float covY = Math.Min(oy + 1f, yBot) - Math.Max((float)oy, yTop);   // vertical coverage (the slanted edges)
                    if (covY <= 0.0005f) { *op = 0; continue; }
                    if (covY > 1f) covY = 1f;
                    float cov = covY * cxv;
                    float yc = Math.Clamp(oy + 0.5f, yTop, yBot);
                    float vc = (yc - yTop) / hgt;                                     // pixel-centre v, clamped into the tile
                    float edge = Math.Min(dxEdge, Math.Min(yc - yTop, yBot - yc));   // px to the nearest straight edge
                    if (xCorner)
                    {
                        float dvv = Math.Min(vc, 1f - vc);
                        if (dvv < rr)
                        {
                            float ex = rr - du, ey = rr - dvv;
                            float dd = MathF.Sqrt(ex * ex + ey * ey);
                            float dist = dd - rr;                                          // > 0: outside the arc
                            float inv = dd > 1e-6f ? 1f / dd : 0f;
                            float nx = ex * inv * gx, ny = ey * inv * gy;
                            float gm = MathF.Sqrt(nx * nx + ny * ny);                      // |d dist / d px|
                            float px = gm > 1e-9f ? dist / gm : (dist > 0f ? 1e9f : -1e9f);  // signed distance in output px
                            float cc = 0.5f - px;
                            if (cc <= 0f) { *op = 0; continue; }
                            if (cc < 1f) cov *= cc;
                            if (-px < edge) edge = -px;
                        }
                    }
                    int A = 0, R = 0, G = 0, B = 0;
                    for (int k = 0; k < n; k++)
                    {
                        float v = (Math.Clamp(oy + (k + 0.5f) / n, yTop, yBot) - yTop) / hgt;
                        float fy = v * h - 0.5f; int iy = (int)MathF.Floor(fy); int wy = (int)((fy - iy) * 256f + 0.5f);
                        int ya = Math.Clamp(iy, 0, h - 1) , yb = Math.Clamp(iy + 1, 0, h - 1);
                        int c = Bilerp(p0, w0, xa0, xb0, wx0, ya, yb, wy);
                        if (two) c = Mix(c, Bilerp(p1, w1, xa1, xb1, wx1, ya, yb, wy), wl);
                        A += (c >> 24) & 0xFF; R += (c >> 16) & 0xFF; G += (c >> 8) & 0xFF; B += c & 0xFF;
                    }
                    if (n > 1) { int half = n >> 1; A = (A + half) / n; R = (R + half) / n; G = (G + half) / n; B = (B + half) / n; }
                    // the faint inner frame: a ~1.5 px lightening just inside every edge
                    float fr = 1.6f - edge;
                    if (fr > 0f)
                    {
                        if (fr > 1f) fr = 1f;
                        float t = FrameAlpha * fr;
                        R += (int)((255 - R) * t); G += (int)((255 - G) * t); B += (int)((255 - B) * t);
                    }
                    int a = (int)(A * cov + 0.5f);
                    if (a <= 0) { *op = 0; continue; }
                    *op = (a << 24) | (((R * a + 127) / 255) << 16) | (((G * a + 127) / 255) << 8) | ((B * a + 127) / 255);   // premultiplied
                }
                // The reflection: the tile's bottom rows mirrored, faded by COLOUR toward the floor while keeping each
                // pixel's coverage as its alpha - so a front cover's reflection occludes the one behind it (like the
                // covers above) instead of blending see-through.
                for (int ry = 0; ry < reflH; ry++)
                {
                    int c = *(int*)(col + (coverH - 1 - ry) * stride);
                    int a = (c >> 24) & 0xFF;
                    int* rp = (int*)(col + (coverH + ry) * stride);
                    if (a == 0) { *rp = 0; continue; }
                    float t = 0.66f + 0.34f * ((ry + 0.5f) / reflH);   // 0.66 at the top -> 1.0 (all floor) at the bottom
                    if (t > 1f) t = 1f;
                    float keep = 1f - t, fa = a * t / 255f;
                    int pr = (int)(((c >> 16) & 0xFF) * keep + j.FloorR * fa + 0.5f);
                    int pg = (int)(((c >> 8) & 0xFF) * keep + j.FloorG * fa + 0.5f);
                    int pb = (int)((c & 0xFF) * keep + j.FloorB * fa + 0.5f);
                    *rp = (a << 24) | (pr << 16) | (pg << 8) | pb;
                }
            }
        }
    }

    /// <summary>Bilinear sample of one mip level: four taps with 8-bit fixed-point weights (0..256), integer maths.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int Bilerp(int* p, int w, int xa, int xb, int wx, int ya, int yb, int wy)
    {
        int ra = ya * w, rb = yb * w;
        int tl = p[ra + xa], tr = p[ra + xb], bl = p[rb + xa], br = p[rb + xb];
        int ix = 256 - wx, iy = 256 - wy, o = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            int top = ((tl >> sh) & 0xFF) * ix + ((tr >> sh) & 0xFF) * wx;   // 0..65280
            int bot = ((bl >> sh) & 0xFF) * ix + ((br >> sh) & 0xFF) * wx;
            int v = (top * iy + bot * wy + (1 << 15)) >> 16;
            o |= (v & 0xFF) << sh;
        }
        return o;
    }

    /// <summary>Blend two ARGB pixels: weight 0..256 toward the second.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mix(int c0, int c1, int w)
    {
        int iw = 256 - w, o = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            int v = (((c0 >> sh) & 0xFF) * iw + ((c1 >> sh) & 0xFF) * w + 128) >> 8;
            o |= (v & 0xFF) << sh;
        }
        return o;
    }

    private void DrawCentreText(Graphics g, float centreY, int coverH)
    {
        int ci = CurrentIndex;
        if (ci < 0 || ci >= _items.Count) return;
        var it = _items[ci];
        int alpha = (int)(255 * Math.Clamp(1f - Math.Abs(_pos - ci), 0f, 1f) * _intro); // fade during a flick + on open/close
        if (alpha < 8) return;
        int y = (int)(centreY + coverH / 2f + coverH * 0.42f + 10);
        var rect = new Rectangle(0, y, Width, 26);
        var tf = _fCentreTitle;
        var sf = _fCentreSub;
        TextRenderer.DrawText(g, it.Title, tf, rect, Color.FromArgb(alpha, Color.White),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(it.Subtitle))
            TextRenderer.DrawText(g, it.Subtitle, sf, new Rectangle(0, y + 26, Width, 22), Color.FromArgb((int)(alpha * 0.8f), Theme.Subtle),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawCloseButton(Graphics g)
    {
        const int sz = 30, m = 14;
        _closeRect = new Rectangle(Width - sz - m, m, sz, sz);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb((int)((_closeHover ? 70 : 40) * _intro), Color.White)))
            g.FillEllipse(b, _closeRect);
        float cx = _closeRect.X + sz / 2f, cy = _closeRect.Y + sz / 2f, r = sz * 0.22f;
        _closePen.Color = Color.FromArgb((int)(220 * _intro), Color.White);
        g.DrawLine(_closePen, cx - r, cy - r, cx + r, cy + r);
        g.DrawLine(_closePen, cx + r, cy - r, cx - r, cy + r);
    }

    /// <summary>Let go of everything cached (the resting sprites and the extracted cover pixels) once the browser is
    /// hidden: reopening repopulates through <see cref="SetItems"/>, which starts from empty anyway.</summary>
    public void Release() => ClearCaches();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tw?.Cancel(); _introTween?.Cancel(); ClearCaches(); _bg?.Dispose(); _vignette?.Dispose(); _live?.Dispose();
            _fCentreTitle.Dispose(); _fCentreSub.Dispose(); _fMode.Dispose(); _fNpChip.Dispose(); _closePen.Dispose();
        }
        base.Dispose(disposing);
    }
}
