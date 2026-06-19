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
    private int _bakedCoverH = -1;                                  // base size the current sprites were baked at (rebake on resize)
    private const int Buckets = 12;                                 // angle steps for the cross-centre rotation (smoother)
    private Bitmap? _bg;                                            // cached backdrop, re-rendered on resize only
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
        g.DrawImageUnscaled(_bg, 0, 0);

        if (_items.Count == 0) { DrawCloseButton(g); return; }

        // Fast per-frame compositing: sprites are pre-rendered at final size, so blit 1:1 (no resampling).
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.SmoothingMode = SmoothingMode.None;

        int H = Height;
        // Sprites bake at this BASE size (depends only on window height). The open/close zoom is applied as a
        // runtime scaled blit — NOT by baking a smaller sprite — so covers actually zoom in (they used to pop in
        // at 84% and stay there). A base-size change (window resize) rebakes the cache so covers re-fit.
        int baseH = Math.Clamp((int)(H * 0.46f), 120, 300);
        if (baseH != _bakedCoverH) { ClearCaches(); _bakedCoverH = baseH; }
        float introScale = 0.84f + 0.16f * Math.Clamp(_intro, 0f, 1f); // open/close zoom (applied as a scaled blit below)
        int coverH = (int)(baseH * introScale);
        int coverW = coverH;
        float cx = Width / 2f, centreY = H * 0.42f;
        float Dv = baseH * 1.85f;                                   // viewer distance in BAKE space (Dv/baseW == Dv/coverW → perspective identical at any zoom)
        float maxAngle = (float)(MaxAngleDeg * Math.PI / 180);
        // The centre cover is flat and "popped" forward; side covers begin just past it (small gap) and
        // recede as an overlapping fan. side1 = first side cover's centre; sideStep = spacing between them.
        float projFull = coverW * (float)Math.Cos(maxAngle);
        float side1 = coverW * 0.5f + coverW * 0.05f + projFull / 2f;
        float sideStep = projFull * 0.52f;
        _stepPx = sideStep;                                         // for drag-to-scrub
        const int range = 5;

        int lo = Math.Max(0, (int)Math.Floor(_pos) - range);
        int hi = Math.Min(_items.Count - 1, (int)Math.Ceiling(_pos) + range);
        // Draw farthest-from-centre first (back) and the centre last (front): each cover overlaps the one
        // further out, the centre on top.
        var order = Enumerable.Range(lo, hi - lo + 1).OrderByDescending(i => Math.Abs(i - _pos)).ToList();

        foreach (int i in order)
            DrawCover(g, i, cx, centreY, coverW, coverH, baseH, Dv, maxAngle, side1, sideStep, introScale);

        // Edge vignette: darken the far side covers toward the screen edges for depth (drawn over them),
        // toned to the theme so it stays in-hue with the backdrop.
        int vw = (int)(Width * 0.24f);
        Color edge = Theme.Blend(Theme.Bg, Color.Black, 0.6);
        using (var lv = new LinearGradientBrush(new Rectangle(0, 0, vw, H), Color.FromArgb(165, edge), Color.FromArgb(0, edge), 0f))
            g.FillRectangle(lv, 0, 0, vw, H);
        using (var rv = new LinearGradientBrush(new Rectangle(Width - vw, 0, vw, H), Color.FromArgb(0, edge), Color.FromArgb(165, edge), 0f))
            g.FillRectangle(rv, Width - vw, 0, vw, H);

        // Evict baked sprites for covers that have scrolled well out of view (bounds memory).
        if (_sprites.Count > 100)
            foreach (var k in _sprites.Keys.Where(k => { int idx = (int)(k >> 12); return idx < lo - 2 || idx > hi + 2; }).ToList())
            { _sprites[k].Dispose(); _sprites.Remove(k); }

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
        for (int i = 0; i < 3; i++) { w[i] = TextRenderer.MeasureText(g, ModeLabels[i], f).Width + padX * 2; total += w[i]; }
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
            TextRenderer.DrawText(g, ModeLabels[i], f, seg, Color.FromArgb((int)((active ? 255 : 205) * _intro), tcol),
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
        const string txt = "Now Playing";
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

    private void DrawCover(Graphics g, int i, float cx, float centreY, int coverW, int coverH, int baseH, float Dv, float maxAngle, float side1, float sideStep, float scale)
    {
        float d = i - _pos;
        float a = Math.Abs(d);
        int s = d < 0 ? -1 : (d > 0 ? 1 : 0);
        int bucket = (int)Math.Round(Math.Clamp(a, 0, 1) * Buckets);  // 0 = flat centre, Buckets = full side angle
        if (bucket == 0) s = 0;
        // |d|<=1: interpolate the centre cover out to the first side slot; beyond: recede by sideStep.
        float o = a <= 1f ? d * side1 : s * (side1 + (a - 1f) * sideStep);
        float Xc = cx + o;

        // Sprite is baked once at the full BASE size; the open/close zoom is a uniform scaled blit so the cache
        // never holds a per-zoom-frame size (which made covers pop in small and never grow / mis-size on resize).
        Bitmap sprite = GetSprite(i, s, bucket, baseH, baseH, Dv, maxAngle);
        float dw = sprite.Width * scale, dh = sprite.Height * scale;
        float left = Xc - dw / 2f, top = centreY - coverH / 2f;
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

    /// <summary>A cover baked (once) into a sprite of cover + faded reflection at a quantised angle — cached
    /// per (index, side, bucket) so animation frames are just blits, not re-warps.</summary>
    private Bitmap GetSprite(int index, int side, int bucket, int coverW, int coverH, float Dv, float maxAngle)
    {
        long key = ((long)index << 12) | ((long)(side + 1) << 8) | (uint)bucket;
        if (_sprites.TryGetValue(key, out var sp)) return sp;
        float theta = maxAngle * bucket / Buckets;
        // Side covers face outward: each cover's OUTER edge is the near (tall) edge and it recedes toward
        // the centre — left cover's near edge = its left edge; right cover's near edge = its right edge.
        sp = BakeSprite(_items[index].Cover, coverW, coverH, theta, Dv, nearRight: side > 0);
        _sprites[key] = sp;
        return sp;
    }

    /// <summary>Render a perspective-warped cover (true foreshortened trapezoid) plus a baked, fade-to-
    /// transparent mirrored reflection beneath it, into one ARGB sprite.</summary>
    private static Bitmap BakeSprite(Bitmap src, int coverW, int coverH, float theta, float Dv, bool nearRight)
    {
        const int SS = 2;                                   // supersample factor (warp hi-res, then downscale)
        int pw = Math.Max(1, (int)Math.Round(coverW * Math.Cos(theta)));
        int reflH = (int)(coverH * 0.5f);
        float sinT = (float)Math.Sin(theta);
        float q = (Dv - coverW / 2f * sinT) / (Dv + coverW / 2f * sinT); // far-edge height fraction

        // 1) Warp at SS× into a PREMULTIPLIED hi-res buffer (manual bilinear, per-column foreshortening +
        //    coverage-AA on the slanted edges), then high-quality-downscale → area-averages the minified art
        //    AND every edge, killing aliasing. Premultiplied so the downscale can't bleed a dark edge halo.
        int pwS = Math.Max(1, pw * SS), chS = coverH * SS;
        var hi = new Bitmap(pwS, chS, PixelFormat.Format32bppPArgb);
        int iw = src.Width, ih = src.Height;
        var sdata = src.LockBits(new Rectangle(0, 0, iw, ih), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var hdata = hi.LockBits(new Rectangle(0, 0, pwS, chS), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        unsafe
        {
            byte* sb = (byte*)sdata.Scan0; int ss = sdata.Stride;
            byte* hb = (byte*)hdata.Scan0; int hs = hdata.Stride;
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
                    int ra = Math.Clamp(r0, 0, ih - 1) * ss, rb = Math.Clamp(r0 + 1, 0, ih - 1) * ss;
                    int px = Bilerp(*(int*)(sb + ra + xa * 4), *(int*)(sb + ra + xb * 4),
                                    *(int*)(sb + rb + xa * 4), *(int*)(sb + rb + xb * 4), xf, yf);
                    int a = (int)(((px >> 24) & 0xFF) * cov);        // final alpha (× edge coverage)
                    // store premultiplied (R*a/255 …) so the downscale interpolates transparency correctly
                    int rr = (((px >> 16) & 0xFF) * a + 127) / 255, gg = (((px >> 8) & 0xFF) * a + 127) / 255, bb = ((px & 0xFF) * a + 127) / 255;
                    *(int*)(hb + oy * hs + ox * 4) = (a << 24) | (rr << 16) | (gg << 8) | bb;
                }
            }
        }
        src.UnlockBits(sdata);
        hi.UnlockBits(hdata);

        var cover = new Bitmap(pw, coverH, PixelFormat.Format32bppPArgb);
        using (var dg = Graphics.FromImage(cover))
        {
            dg.InterpolationMode = InterpolationMode.HighQualityBilinear; // 2×→1× ≈ box average, no ringing
            dg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            dg.CompositingQuality = CompositingQuality.HighQuality;
            dg.DrawImage(hi, new Rectangle(0, 0, pw, coverH), 0, 0, pwS, chS, GraphicsUnit.Pixel);
        }
        hi.Dispose();

        var sprite = new Bitmap(pw, coverH + reflH, PixelFormat.Format32bppPArgb); // premultiplied → fastest to blit
        using (var g = Graphics.FromImage(sprite))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(cover, 0, 0);
            // Mirror the cover straight below it; only the top reflH shows.
            var flip = new[] { new PointF(0, coverH * 2), new PointF(pw, coverH * 2), new PointF(0, coverH) };
            g.SetClip(new RectangleF(0, coverH, pw, reflH));
            g.DrawImage(cover, flip, new RectangleF(0, 0, pw, coverH), GraphicsUnit.Pixel);
            g.ResetClip();
        }
        cover.Dispose();
        // Fade the reflection with a smooth, continuous top→bottom alpha ramp (no banded seams).
        FadeReflection(sprite, coverH, reflH);
        return sprite;
    }

    /// <summary>Multiply the reflection rows' alpha by a smooth top→bottom ramp (× a base dimming). The
    /// sprite is premultiplied ARGB, so scaling all four bytes scales the effective alpha while keeping colour.</summary>
    private static void FadeReflection(Bitmap sprite, int coverH, int reflH)
    {
        var rect = new Rectangle(0, coverH, sprite.Width, reflH);
        var data = sprite.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        unsafe
        {
            byte* b0 = (byte*)data.Scan0; int stride = data.Stride, w = sprite.Width;
            for (int y = 0; y < reflH; y++)
            {
                float f = 0.34f * (1f - (y + 0.5f) / reflH);   // base reflection alpha × linear fade to 0
                if (f < 0f) f = 0f;
                byte* row = b0 + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * 4;
                    p[0] = (byte)(p[0] * f); p[1] = (byte)(p[1] * f); p[2] = (byte)(p[2] * f); p[3] = (byte)(p[3] * f);
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
        var dead = _sprites.Keys.Where(k => (int)(k >> 12) == index).ToList();
        foreach (var k in dead) { _sprites[k].Dispose(); _sprites.Remove(k); }
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
        foreach (var b in _sprites.Values) b.Dispose();
        _sprites.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); _introTween?.Cancel(); ClearCaches(); _bg?.Dispose(); }
        base.Dispose(disposing);
    }
}
