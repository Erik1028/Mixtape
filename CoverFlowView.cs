using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iPodCommander;

/// <summary>
/// A Cover-Flow album browser: the centre cover faces the viewer flat; covers to each side recede in
/// true perspective (a foreshortened trapezoid, not just a shear) with a glossy mirrored reflection,
/// and the deck coasts smoothly between covers. Navigate with the mouse wheel, arrow keys, or by
/// clicking a side cover; click the centre cover (or Enter) to activate it; Esc closes.
///
/// True perspective isn't expressible with GDI+'s 3-point (affine) DrawImage, so each side cover is
/// rendered by a per-column projective warp: for each output column we compute its perspective-correct
/// source x and its foreshortened height. Fully-rotated side covers all share one shape, so their warp
/// is cached per cover; only the 1–2 covers crossing the centre are warped live each frame.
/// </summary>
internal sealed class CoverFlowView : Control
{
    public sealed record Item(Bitmap Cover, string Title, string Subtitle, object? Tag);

    private readonly List<Item> _items = new();
    private float _pos;        // current fractional centre index (animates toward _target)
    private int _target;       // index we're coasting to
    private Tween? _tw;
    private readonly Dictionary<long, Bitmap> _sprites = new();    // baked cover+reflection per (index, side, angle-bucket)
    private readonly object _spritesLock = new();                  // _sprites is read on the UI thread, written on the bake worker

    // ---- background baking ----
    // BakeSprite (per-column projective warp + downscale + reflection fade) costs ~10ms at full-screen; running it
    // on the paint/anim thread stalled the UI whenever a fresh angle was needed (the scroll "lag"). Instead a single
    // worker thread does every bake: the paint thread draws the nearest ALREADY-baked angle and queues the exact one,
    // which the worker fills in a few ms later. A generation counter discards bakes whose size/items changed mid-flight.
    // The worker bakes from an immutable pixel ARRAY, never the live Bitmap — GDI+ Bitmaps aren't thread-safe, and
    // the paint thread still reads the raw covers (the flat-card fallback), so they must stay UI-thread-only.
    private readonly struct BakeReq { public readonly long Key; public readonly int[] SrcPx; public readonly int SrcW, SrcH, BaseH, Side, Bucket, Gen;
        public BakeReq(long k, int[] px, int sw, int sh, int bh, int side, int bucket, int gen) { Key = k; SrcPx = px; SrcW = sw; SrcH = sh; BaseH = bh; Side = side; Bucket = bucket; Gen = gen; } }
    private readonly Dictionary<long, (int[] px, int w, int h)> _srcPx = new();  // per (index,ver) cover pixels, extracted once on the UI thread
    private readonly object _qLock = new();
    private readonly Stack<BakeReq> _queue = new();               // LIFO → the most recently needed angle bakes first
    private readonly HashSet<long> _pending = new();             // keys already queued or in flight (dedup)
    private readonly AutoResetEvent _bakeSignal = new(false);
    private Thread? _baker;
    private volatile bool _bakerStop;
    private volatile int _gen;                                   // bumped on ClearCaches; a bake from an older gen is dropped
    private readonly Dictionary<int, int> _coverVer = new();     // per-index cover version (bumped when a real cover streams in) → baked into the key so a stale placeholder bake can't shadow it
    private byte[]? _hiBuf;                                      // worker-only reusable supersample buffer (avoids ~3MB LOH churn → GC pauses per bake)
    private System.Runtime.InteropServices.GCHandle _hiPin;     // pins _hiBuf so a Bitmap can wrap it for the GDI+ downscale
    private int _bakedCoverH = -1;                                  // base size the current sprites were baked at (rebake on resize)
    private const int Buckets = 20;                                 // angle steps for the cross-centre rotation (more = smoother)
    private Bitmap? _bg;                                            // cached backdrop, re-rendered on resize only
    private Bitmap? _vignette;                                      // cached edge-darkening overlay (re-rendered on resize only)
    private float _lastPaintPos;                                    // _pos at the previous paint → per-frame scroll speed
    private bool _fast;                                             // moving fast this frame → coarsen the warp buckets
    private float _intro = 1f;                                      // open/close zoom+fade (1 = fully shown)
    private Tween? _introTween;
    private readonly List<(int index, RectangleF rect)> _hit = new();
    private Rectangle _closeRect;
    private bool _closeHover;
    // drag-to-scrub
    private bool _mouseDown, _dragging;
    private int _downX;
    private float _downPos, _stepPx = 60f;     // _stepPx = horizontal px between covers (cached from paint)
    // currently-playing album (set by the host) → marker on its cover + a "Now Playing" chip
    private object? _playingTag;
    private Rectangle _npChip;
    private bool _npChipHover;
    // Songs / Albums / Artists segmented toggle (top-centre) — the host rebuilds the deck on change.
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

    private const float MaxAngleDeg = 70f;   // steeper side-cover angle (classic Cover Flow look)

    public CoverFlowView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        TabStop = true;
    }

    public int CurrentIndex => Math.Clamp((int)Math.Round(_pos), 0, Math.Max(0, _items.Count - 1));
    public bool Settled => Math.Abs(_pos - _target) < 0.01f;

    public void SetItems(IEnumerable<Item> items, int start = 0)
    {
        ClearCaches();
        _items.Clear();
        _items.AddRange(items);
        _target = Math.Clamp(start, 0, Math.Max(0, _items.Count - 1));
        _pos = _target;
        Invalidate();
    }

    /// <summary>Swap in a real cover for an item (covers stream in after the view is shown). The bitmap is
    /// owned by the caller (e.g. ArtworkService's cache) — never disposed here; only our warp caches are.</summary>
    public void SetCover(int index, Bitmap cover)
    {
        if (index < 0 || index >= _items.Count || cover is null) return;
        _items[index] = _items[index] with { Cover = cover };
        EvictIndex(index);   // drop baked sprites for this item so they re-bake with the real art
        Invalidate();
    }

    // ---- navigation ----

    public void MoveTo(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        if (index == _target) return;
        _target = index;
        _tw?.Cancel();
        if (!Anim.MotionEnabled) { _pos = _target; Invalidate(); return; }
        float from = _pos, to = _target;
        // Coast time scales gently with distance (snappy single steps, a longer glide for big jumps) and
        // settles with a smooth deceleration. Continuing from the current position keeps rapid flicks fluid.
        double dur = Math.Clamp(260 + 95 * Math.Sqrt(Math.Abs(to - from)), 260, 620);
        _tw = Anim.Run(dur, v => { _pos = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutQuint);
    }
    public void Move(int delta) => MoveTo(_target + delta);

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
        _mouseDown = true; _dragging = false; _downX = e.X; _downPos = _pos;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown)
        {
            if (!_dragging && Math.Abs(e.X - _downX) > 4) _dragging = true;
            if (_dragging)
            {
                _tw?.Cancel();
                _pos = Math.Clamp(_downPos - (e.X - _downX) / Math.Max(1f, _stepPx), 0, Math.Max(0, _items.Count - 1));
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
        if (_dragging) { _dragging = false; MoveTo((int)Math.Round(_pos)); return; } // snap to nearest cover
        int hit = HitTest(e.Location);                                               // a click (no drag)
        if (hit < 0) return;
        if (hit == CurrentIndex && Settled) ActivateCentre(); else MoveTo(hit);
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
        var g = e.Graphics;
        _hit.Clear();
        // Per-frame scroll speed → once moving more than a touch, coarsen the rotation buckets so far fewer distinct
        // perspective sprites are baked (they blur past anyway) → the background worker keeps up and covers don't
        // pop in; precise rotation returns the moment it slows / settles. (Was 0.4 — too high; medium scrolls fell
        // through to fine buckets and starved the worker.)
        _fast = Math.Abs(_pos - _lastPaintPos) > 0.25f;
        _lastPaintPos = _pos;

        // Backdrop: a dark vertical gradient, cached as a bitmap (re-rendered only when the size changes)
        // and blitted 1:1 each frame — far cheaper than gradient-filling the whole control every paint.
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
            using (var gp = new System.Drawing.Drawing2D.GraphicsPath())
            {
                var er = new RectangleF(_bg.Width * 0.06f, -_bg.Height * 0.25f, _bg.Width * 0.88f, _bg.Height * 1.05f);
                gp.AddEllipse(er);
                using var pgb = new PathGradientBrush(gp)
                { CenterColor = Theme.Blend(Theme.Bg, Color.White, 0.10), SurroundColors = new[] { Color.FromArgb(0, Theme.Bg) }, CenterPoint = new PointF(_bg.Width / 2f, _bg.Height * 0.40f) };
                bgg.FillPath(pgb, gp);
            }
        }
        var prevCM = g.CompositingMode;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;   // _bg is opaque → skip the per-pixel alpha blend on the big full-screen blit
        g.DrawImageUnscaled(_bg, 0, 0);
        g.CompositingMode = prevCM;                                                // covers + vignette need SourceOver

        if (_items.Count == 0) { DrawCloseButton(g); return; }

        // Fast per-frame compositing: sprites are pre-rendered at final size, so blit 1:1 (no resampling).
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.SmoothingMode = SmoothingMode.None;

        int H = Height;
        // Sprites bake at this BASE size. It tracks the window HEIGHT (covers fill ~46% of it) but is also held
        // under a fraction of the WIDTH so a wide / full-screen window grows the covers (bigger deck) instead of
        // pinning them at a small cap — they used to stay 300px on any large window, leaving a narrow band with
        // big black side margins. The open/close zoom is a runtime scaled blit (not a smaller bake), so covers
        // actually zoom in; a base-size change (resize / maximize) rebakes the cache so covers re-fit.
        int baseH = Math.Clamp((int)Math.Min(H * 0.46f, Width * 0.34f), 130, 420);
        if (baseH != _bakedCoverH) { ClearCaches(); _bakedCoverH = baseH; }
        float introScale = 0.84f + 0.16f * Math.Clamp(_intro, 0f, 1f); // open/close zoom (applied as a scaled blit below)
        int coverH = (int)(baseH * introScale);
        int coverW = coverH;
        float cx = Width / 2f, centreY = H * 0.42f;
        float maxAngle = (float)(MaxAngleDeg * Math.PI / 180);      // baker derives viewer distance Dv = baseH*1.85 itself
        // The centre cover is flat and "popped" forward; the side covers recede as an overlapping fan. side1 =
        // first side cover's centre; sideStep = spacing between side covers. Both are tuned so the first side
        // cover slips slightly UNDER the centre cover (no backdrop gap line), and side covers overlap enough
        // that their foreshortened (sloped) tops don't leave a dark backdrop wedge between neighbours.
        float projFull = coverW * (float)Math.Cos(maxAngle);
        float side1 = coverW * 0.5f + projFull / 2f - coverW * 0.04f;   // overlap the centre cover by ~4% (was a +5% GAP → a black seam line)
        float sideStep = projFull * 0.52f;
        _stepPx = sideStep;                                         // for drag-to-scrub
        // Fan out enough covers to reach the screen edges (capped for perf), so a wide / full-screen window
        // shows a full-width deck rather than a short fan stranded in the middle.
        int range = Math.Clamp((int)Math.Ceiling((Width / 2f - side1) / sideStep) + 2, 5, 8);

        int lo = Math.Max(0, (int)Math.Floor(_pos) - range);
        int hi = Math.Min(_items.Count - 1, (int)Math.Ceiling(_pos) + range);
        // Draw farthest-from-centre first (back) and the centre last (front): each cover overlaps the one
        // further out, the centre on top.
        var order = Enumerable.Range(lo, hi - lo + 1).OrderByDescending(i => Math.Abs(i - _pos)).ToList();

        foreach (int i in order)
            DrawCover(g, i, cx, centreY, coverW, coverH, baseH, maxAngle, side1, sideStep, introScale);

        // Edge vignette: darken the far side covers toward the screen edges for depth (drawn over them).
        // Cached as a transparent overlay — and blitted as only its two non-empty EDGE STRIPS (the wide middle is
        // fully transparent, so a full-width alpha blit just churned ~half the pixels for nothing).
        int vw = (int)(Width * 0.24f);
        if (_vignette is null || _vignette.Width != Width || _vignette.Height != H)
        {
            _vignette?.Dispose();
            _vignette = new Bitmap(Math.Max(1, Width), Math.Max(1, H), PixelFormat.Format32bppPArgb);
            using var vg = Graphics.FromImage(_vignette);
            Color edge = Theme.Blend(Theme.Bg, Color.Black, 0.6);
            // ⚠️ The gradient-brush rect is 1px WIDER than the fill on each end: a LinearGradientBrush renders its
            // very first column at the WRAPPED (end) colour — here that put a hard dark line where the right
            // vignette starts. Pushing the brush edges outside the fill region hides that buggy column.
            using (var lv = new LinearGradientBrush(new Rectangle(-1, 0, vw + 2, H), Color.FromArgb(165, edge), Color.FromArgb(0, edge), 0f))
                vg.FillRectangle(lv, 0, 0, vw, H);
            using (var rv = new LinearGradientBrush(new Rectangle(Width - vw - 1, 0, vw + 2, H), Color.FromArgb(0, edge), Color.FromArgb(165, edge), 0f))
                vg.FillRectangle(rv, Width - vw, 0, vw, H);
        }
        g.DrawImage(_vignette, new Rectangle(0, 0, vw, H), 0, 0, vw, H, GraphicsUnit.Pixel);                       // left strip
        g.DrawImage(_vignette, new Rectangle(Width - vw, 0, vw, H), Width - vw, 0, vw, H, GraphicsUnit.Pixel);     // right strip

        // Evict baked sprites + cover-pixel arrays for covers that have scrolled well out of view (bounds memory).
        if (_sprites.Count > 100)
            lock (_spritesLock)
                foreach (var k in _sprites.Keys.Where(k => { int idx = (int)(k >> 12); return idx < lo - 2 || idx > hi + 2; }).ToList())
                { _sprites[k].Dispose(); _sprites.Remove(k); }
        if (_srcPx.Count > 40)
            foreach (var k in _srcPx.Keys.Where(k => { int idx = (int)(k >> 2); return idx < lo - 2 || idx > hi + 2; }).ToList())
                _srcPx.Remove(k);

        DrawCentreText(g, centreY, coverH);
        DrawNowPlayingChip(g);
        DrawModeSwitch(g);
        DrawCloseButton(g);
    }

    /// <summary>A Songs / Albums / Artists segmented toggle centred at the top — clicking a segment raises
    /// <see cref="ModeChanged"/> so the host rebuilds the deck.</summary>
    private void DrawModeSwitch(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var f = Theme.UiFont(9f, FontStyle.Bold);
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
        using var f = Theme.UiFont(8.75f, FontStyle.Bold);
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

    private void DrawCover(Graphics g, int i, float cx, float centreY, int coverW, int coverH, int baseH, float maxAngle, float side1, float sideStep, float scale)
    {
        float d = i - _pos;
        float a = Math.Abs(d);
        int s = d < 0 ? -1 : (d > 0 ? 1 : 0);
        int bucket = (int)Math.Round(Math.Clamp(a, 0, 1) * Buckets);  // 0 = flat centre, Buckets = full side angle
        if (_fast && bucket > 0 && bucket < Buckets)                  // flicking → snap to every-5th bucket so only a
            bucket = Math.Clamp((int)Math.Round(bucket / 5.0) * 5, 0, Buckets); // handful of sprites bake, then get reused
        if (bucket == 0) s = 0;
        // |d|<=1: interpolate the centre cover out to the first side slot; beyond: recede by sideStep.
        float o = a <= 1f ? d * side1 : s * (side1 + (a - 1f) * sideStep);
        float Xc = cx + o;

        // Sprite is baked once at the full BASE size; the open/close zoom is a uniform scaled blit so the cache
        // never holds a per-zoom-frame size (which made covers pop in small and never grow / mis-size on resize).
        Bitmap? sprite = GetSprite(i, s, bucket, baseH);
        float top = centreY - coverH / 2f;
        if (sprite is null)
        {
            // No baked angle for this cover yet (it just entered during a fast flick and the worker hasn't reached
            // it). Rather than leave a gap (pop-in), draw the raw cover as a quick width-squashed flat card so a
            // cover is ALWAYS present; the proper perspective sprite replaces it within a frame or two. Only ever
            // seen mid-flick on the leading edge, where motion + the edge vignette hide the missing slant.
            float projW = coverW * (float)Math.Cos(maxAngle * Math.Min(bucket, Buckets) / (float)Buckets) * scale;
            float ch = coverH * scale;
            var im0 = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_items[i].Cover, Xc - projW / 2f, top, projW, ch);
            g.InterpolationMode = im0;
            _hit.Add((i, new RectangleF(Xc - projW / 2f, top, projW, ch)));
            return;
        }
        float dw = sprite.Width * scale, dh = sprite.Height * scale;
        float left = Xc - dw / 2f;
        _hit.Add((i, new RectangleF(left, top, dw, coverH)));
        if (scale >= 0.999f)
            g.DrawImageUnscaled(sprite, (int)Math.Round(left), (int)Math.Round(top)); // resting: 1:1, no resample
        else
        {
            var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(sprite, left, top, dw, dh);                                    // open/close zoom frames: smooth scale
            g.InterpolationMode = im;
        }
    }

    // Sprite cache key: index | cover-version | side | angle-bucket (all non-overlapping bit fields).
    private static long SpriteKey(int index, int ver, int side, int bucket) =>
        ((long)index << 12) | ((long)(ver & 3) << 10) | ((long)(side + 1) << 8) | (uint)bucket;
    private int Ver(int index) => _coverVer.TryGetValue(index, out var v) ? v : 0;   // UI thread only

    /// <summary>Return the baked sprite for (index, side, bucket) if it's ready; otherwise queue it for the
    /// background baker and return the NEAREST already-baked angle for this cover (or null if none yet). The
    /// paint thread never bakes — so a fresh angle can never stall the animation; the worker fills it in within
    /// a few ms and a repaint is requested. Sprites are baked once per (index, version, side, bucket) and reused.</summary>
    private Bitmap? GetSprite(int index, int side, int bucket, int baseH)
    {
        int ver = Ver(index);
        long key = SpriteKey(index, ver, side, bucket);
        lock (_spritesLock) if (_sprites.TryGetValue(key, out var sp)) return sp;
        Enqueue(index, side, bucket, baseH, key);
        // Guarantee a fallback angle for this cover: also queue its full-side anchor (baked once, then reused) so
        // NearestCached always has something close to draw while the exact in-between angle is still baking.
        if (bucket != 0 && bucket != Buckets)
            Enqueue(index, side, Buckets, baseH, SpriteKey(index, ver, side, Buckets));
        return NearestCached(index, ver, side, bucket);
    }

    private void Enqueue(int index, int side, int bucket, int baseH, long key)
    {
        EnsureBaker();
        var (px, w, h) = GetSrcPx(index);                   // extract on the UI thread → worker reads the array, never the Bitmap
        lock (_qLock)
        {
            if (!_pending.Add(key)) return;                 // already queued or in flight
            _queue.Push(new BakeReq(key, px, w, h, baseH, side, bucket, _gen));
        }
        _bakeSignal.Set();
    }

    /// <summary>Extract a cover's pixels into an immutable ARGB array (once per index+version), cached. Called on
    /// the UI thread so the live Bitmap is never locked while the paint thread (flat-card fallback) reads it; the
    /// worker then bakes from the array with no GDI+ involvement at all.</summary>
    private (int[] px, int w, int h) GetSrcPx(int index)
    {
        long k = ((long)index << 2) | (uint)(Ver(index) & 3);
        if (_srcPx.TryGetValue(k, out var e)) return e;
        var bmp = _items[index].Cover;
        int w = bmp.Width, h = bmp.Height;
        var arr = new int[w * h];
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, arr, 0, w * h);   // 32bpp → stride == w*4, no row padding
        bmp.UnlockBits(d);
        e = (arr, w, h); _srcPx[k] = e; return e;
    }

    /// <summary>The cached sprite for this cover whose angle bucket is closest to the one requested (search
    /// outward from the target), or null if no angle for this cover is baked yet.</summary>
    private Bitmap? NearestCached(int index, int ver, int side, int bucket)
    {
        lock (_spritesLock)
        {
            for (int r = 0; r <= Buckets; r++)
            {
                int b1 = bucket - r, b2 = bucket + r;
                if (b1 >= 0 && _sprites.TryGetValue(SpriteKey(index, ver, side, b1), out var s1)) return s1;
                if (r > 0 && b2 <= Buckets && _sprites.TryGetValue(SpriteKey(index, ver, side, b2), out var s2)) return s2;
            }
        }
        return null;
    }

    private void EnsureBaker()
    {
        if (_baker is not null) return;                     // only ever called from the UI thread → no double-start
        _baker = new Thread(BakerLoop) { IsBackground = true, Name = "CoverFlowBaker", Priority = ThreadPriority.BelowNormal };
        _baker.Start();
    }

    /// <summary>The single background bake worker. Pops the most-recently-requested angle, warps+downscales+
    /// reflects it (the ~10ms cost that used to hitch the paint thread), stores it, and asks for a repaint.
    /// A generation mismatch (size/items changed since the request) discards the result so nothing stale lands.</summary>
    private void BakerLoop()
    {
        while (!_bakerStop)
        {
            BakeReq req; bool has;
            lock (_qLock) { has = _queue.Count > 0; if (has) req = _queue.Pop(); else req = default; }
            if (!has) { _bakeSignal.WaitOne(250); continue; }
            if (req.Gen != _gen) { lock (_qLock) _pending.Remove(req.Key); continue; }   // stale before we even start
            Bitmap? sprite = null;
            try
            {
                float theta = (float)(MaxAngleDeg * Math.PI / 180) * req.Bucket / Buckets;
                // Only the flat focused CENTRE cover (bucket 0) bakes at 2× supersample (it's big and crisp on screen);
                // every angled side cover bakes at 1× — they're foreshortened, minified and edge-vignetted, so 2× was
                // imperceptible there but ~4× the cost, which starved the worker (covers popped in) during fast scroll.
                int ss = (req.Bucket == 0) ? 2 : 1;
                // Side covers face outward: the OUTER edge is the near (tall) edge; it recedes toward the centre.
                sprite = BakeSprite(req.SrcPx!, req.SrcW, req.SrcH, req.BaseH, req.BaseH, theta, req.BaseH * 1.85f, nearRight: req.Side > 0, ss);  // always set for a real (popped) req
            }
            catch { sprite = null; }                        // cover bitmap evicted/disposed mid-bake → skip; it'll be re-requested
            bool kept = false;
            if (sprite is not null)
                lock (_spritesLock)
                    if (req.Gen == _gen && !_sprites.ContainsKey(req.Key)) { _sprites[req.Key] = sprite; kept = true; }
            if (sprite is not null && !kept) sprite.Dispose();
            lock (_qLock) _pending.Remove(req.Key);
            if (kept) RequestRepaint();
        }
    }

    private void RequestRepaint()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed) Invalidate(); })); } catch { /* handle gone */ }
    }

    /// <summary>Render a perspective-warped cover (true foreshortened trapezoid) plus a baked, fade-to-
    /// transparent mirrored reflection beneath it, into one ARGB sprite. Worker-thread only — it reuses a
    /// single pinned supersample buffer (<see cref="_hiBuf"/>) instead of allocating one per bake, so the
    /// scroll no longer triggers Gen2 GC pauses from ~3MB-per-bake Large-Object-Heap churn.</summary>
    private Bitmap BakeSprite(int[] srcPx, int iw, int ih, int coverW, int coverH, float theta, float Dv, bool nearRight, int sup)
    {
        int pw = Math.Max(1, (int)Math.Round(coverW * Math.Cos(theta)));
        int reflH = (int)(coverH * 0.5f);
        float sinT = (float)Math.Sin(theta);
        float q = (Dv - coverW / 2f * sinT) / (Dv + coverW / 2f * sinT); // far-edge height fraction

        // 1) Warp at ss× into a PREMULTIPLIED hi-res buffer (manual bilinear, per-column foreshortening +
        //    coverage-AA on the slanted edges), then high-quality-downscale → area-averages the minified art
        //    AND every edge, killing aliasing. Premultiplied so the downscale can't bleed a dark edge halo.
        int pwS = Math.Max(1, pw * sup), chS = coverH * sup;
        int hStride = pwS * 4, hNeed = hStride * chS;
        if (_hiBuf is null || _hiBuf.Length < hNeed)            // (re)allocate + pin only when a bigger buffer is needed
        {
            if (_hiPin.IsAllocated) _hiPin.Free();
            _hiBuf = new byte[hNeed];
            _hiPin = System.Runtime.InteropServices.GCHandle.Alloc(_hiBuf, System.Runtime.InteropServices.GCHandleType.Pinned);
        }
        Array.Clear(_hiBuf, 0, hNeed);                          // transparent ground (warp only writes inside the trapezoid)
        IntPtr hiPtr = _hiPin.AddrOfPinnedObject();
        unsafe
        {
        fixed (int* sp = srcPx)                                  // read the cover pixels straight from the shared array (no GDI+ lock)
        {
            byte* hb = (byte*)hiPtr; int hs = hStride;
            for (int ox = 0; ox < pwS; ox++)
            {
                float sFrac = nearRight ? 1f - (ox + 0.5f) / pwS : (ox + 0.5f) / pwS; // 0 near edge → 1 far edge
                float hgt = chS * ((1 - sFrac) + sFrac * q);
                float yTopF = (chS - hgt) / 2f, yBotF = (chS + hgt) / 2f;
                float spanF = Math.Max(1f, yBotF - yTopF);
                float u = sFrac * q / ((1 - sFrac) + sFrac * q);     // perspective-correct source x (0 near → 1 far)
                float fx = (nearRight ? iw * (1 - u) : iw * u) - 0.5f;
                int x0 = (int)Math.Floor(fx); float xf = fx - x0;
                int xa = Math.Clamp(x0, 0, iw - 1), xb = Math.Clamp(x0 + 1, 0, iw - 1);
                int y0 = Math.Max(0, (int)Math.Floor(yTopF)), y1 = Math.Min(chS - 1, (int)Math.Ceiling(yBotF) - 1);
                for (int oy = y0; oy <= y1; oy++)
                {
                    float cov = Math.Min(oy + 1f, yBotF) - Math.Max((float)oy, yTopF); // edge coverage 0..1
                    if (cov <= 0f) continue;
                    if (cov > 1f) cov = 1f;
                    float fy = (oy + 0.5f - yTopF) / spanF * ih - 0.5f;
                    int r0 = (int)Math.Floor(fy); float yf = fy - r0;
                    int ra = Math.Clamp(r0, 0, ih - 1) * iw, rb = Math.Clamp(r0 + 1, 0, ih - 1) * iw;
                    int px = Bilerp(sp[ra + xa], sp[ra + xb], sp[rb + xa], sp[rb + xb], xf, yf);
                    int a = (int)(((px >> 24) & 0xFF) * cov);        // final alpha (× edge coverage)
                    // store premultiplied (R*a/255 …) so the downscale interpolates transparency correctly
                    int rr = (((px >> 16) & 0xFF) * a + 127) / 255, gg = (((px >> 8) & 0xFF) * a + 127) / 255, bb = ((px & 0xFF) * a + 127) / 255;
                    *(int*)(hb + oy * hs + ox * 4) = (a << 24) | (rr << 16) | (gg << 8) | bb;
                }
            }
        }
        }

        // 2) High-quality-downscale the supersample buffer straight into the sprite — the cover at the top and a
        //    vertically-mirrored copy in the reflection band below — wrapping the pooled buffer in a thin Bitmap
        //    header (no pixel copy). Skips the old intermediate `cover` bitmap entirely (one less LOH alloc/bake).
        var sprite = new Bitmap(pw, coverH + reflH, PixelFormat.Format32bppPArgb); // premultiplied → fastest to blit
        using (var hi = new Bitmap(pwS, chS, hStride, PixelFormat.Format32bppPArgb, hiPtr))
        using (var g = Graphics.FromImage(sprite))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear; // 2×→1× ≈ box average, no ringing
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(hi, new Rectangle(0, 0, pw, coverH), 0, 0, pwS, chS, GraphicsUnit.Pixel);   // cover
            // Mirror the cover straight below it; only the top reflH shows.
            var flip = new[] { new PointF(0, coverH * 2), new PointF(pw, coverH * 2), new PointF(0, coverH) };
            g.SetClip(new RectangleF(0, coverH, pw, reflH));
            g.DrawImage(hi, flip, new RectangleF(0, 0, pwS, chS), GraphicsUnit.Pixel);              // reflection
            g.ResetClip();
        }
        // Fade the reflection by colour toward the floor (NOT by alpha) so reflections OCCLUDE each other where
        // they overlap — exactly like the covers above — instead of blending see-through.
        FadeReflection(sprite, coverH, reflH);
        return sprite;
    }

    /// <summary>Fade the reflection by COLOUR toward the floor colour (≈ the backdrop) while KEEPING each
    /// pixel's coverage as its alpha — so a front cover's reflection occludes the one behind it (like the covers
    /// above) instead of blending see-through. Over the bare backdrop it looks the same as the old alpha fade
    /// (colour lerps 0.66→1.0 to the floor, matching a 0.34→0 alpha mirror over that floor); only the OVERLAP
    /// changes. Premultiplied ARGB: un-premultiply, lerp the colour, keep alpha, re-premultiply.</summary>
    private static void FadeReflection(Bitmap sprite, int coverH, int reflH)
    {
        const float floorB = 22f, floorG = 27f, floorR = 16f;   // ≈ Blend(Theme.Bg, Black, 0.22) in B,G,R byte order
        var rect = new Rectangle(0, coverH, sprite.Width, reflH);
        var data = sprite.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        unsafe
        {
            byte* b0 = (byte*)data.Scan0; int stride = data.Stride, w = sprite.Width;
            for (int y = 0; y < reflH; y++)
            {
                float t = 0.66f + 0.34f * ((y + 0.5f) / reflH);   // blend-to-floor: 0.66 at the top → 1.0 at the bottom
                if (t > 1f) t = 1f;
                float keep = 1f - t;
                byte* row = b0 + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 4;
                    int a = p[3];
                    if (a == 0) continue;                         // transparent corner → leave it (no occlusion there)
                    float inv = 255f / a;                          // un-premultiply to the mirror's true colour
                    float B = p[0] * inv * keep + floorB * t;
                    float G = p[1] * inv * keep + floorG * t;
                    float R = p[2] * inv * keep + floorR * t;
                    float am = a / 255f;                           // re-premultiply; alpha (coverage) unchanged → still occludes
                    p[0] = (byte)(B * am); p[1] = (byte)(G * am); p[2] = (byte)(R * am);
                }
            }
        }
        sprite.UnlockBits(data);
    }

    /// <summary>Bilinear blend of four ARGB pixels (tl, tr, bl, br) by horizontal/vertical fractions.</summary>
    private static int Bilerp(int tl, int tr, int bl, int br, float xf, float yf)
    {
        int o = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            float top = ((tl >> sh) & 0xFF) * (1 - xf) + ((tr >> sh) & 0xFF) * xf;
            float bot = ((bl >> sh) & 0xFF) * (1 - xf) + ((br >> sh) & 0xFF) * xf;
            int v = (int)(top * (1 - yf) + bot * yf + 0.5f);
            o |= (v & 0xFF) << sh;
        }
        return o;
    }

    private void EvictIndex(int index)
    {
        foreach (var k in _srcPx.Keys.Where(k => (int)(k >> 2) == index).ToList()) _srcPx.Remove(k);  // drop the old cover's pixels
        _coverVer[index] = Ver(index) + 1;   // bump version so any in-flight stale-cover bake lands on a dead key (never returned)
        lock (_spritesLock)
        {
            var dead = _sprites.Keys.Where(k => (int)(k >> 12) == index).ToList();
            foreach (var k in dead) { _sprites[k].Dispose(); _sprites.Remove(k); }
        }
        lock (_qLock) { _pending.RemoveWhere(k => (int)(k >> 12) == index); }
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
        using var tf = Theme.DisplayFont(13f, FontStyle.Bold);
        using var sf = Theme.UiFont(10f);
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
        using var pen = new Pen(Color.FromArgb((int)(220 * _intro), Color.White), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
        g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
    }

    private void ClearCaches()
    {
        lock (_qLock) { _queue.Clear(); _pending.Clear(); }          // abandon queued bakes
        lock (_spritesLock)
        {
            _gen++;                                                  // any bake still in flight will be discarded on completion
            foreach (var b in _sprites.Values) b.Dispose();
            _sprites.Clear();
        }
        _srcPx.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bakerStop = true; _bakeSignal.Set(); _baker?.Join(500); _bakeSignal.Dispose();
            if (_hiPin.IsAllocated) _hiPin.Free();              // safe: worker has stopped, so no bake is mid-flight
            _tw?.Cancel(); _introTween?.Cancel(); ClearCaches(); _bg?.Dispose(); _vignette?.Dispose();
        }
        base.Dispose(disposing);
    }
}
