using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A small horizontal themed slider (track + accent fill + white knob). Fires <see cref="Changed"/> live
/// while dragging and <see cref="Committed"/> once on release — so a host can preview live but persist only once.</summary>
internal sealed class MiniSlider : Control
{
    public event Action<int>? Changed;
    public event Action<int>? Committed;

    private int _min, _max = 100, _value;
    private bool _drag;

    public int Minimum { get => _min; set { _min = value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; Invalidate(); } }
    public int Value { get => _value; set { int v = Math.Clamp(value, _min, _max); if (v != _value) { _value = v; Invalidate(); } } }

    public MiniSlider()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 24;
    }

    // The track runs between the two pictograms; these are the x of each track end (inset to clear the glyphs).
    private float TrackX0 => 32;
    private float TrackX1 => Width - 34;

    private void SetFromX(int x)
    {
        float frac = Math.Clamp((x - TrackX0) / Math.Max(1, TrackX1 - TrackX0), 0, 1);
        int v = (int)Math.Round(_min + frac * (_max - _min));
        if (v != _value) { _value = v; Invalidate(); Changed?.Invoke(_value); }
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (_drag) { _drag = false; Committed?.Invoke(_value); } base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        // Recessed rounded frame — matches the search box that shares this header row (1.5px inset so the pill's
        // rounded ends don't clip against the control edge).
        float fpad = 1.5f, fh = Height - 2 * fpad;
        var frame = new RectangleF(fpad, fpad, Width - 2 * fpad, fh);
        using (var fb = new SolidBrush(Theme.Blend(Theme.Bg, Color.Black, 0.30)))
        using (var fr = Theme.RoundedRect(frame, fh / 2f)) g.FillPath(fb, fr);
        using (var fpn = new Pen(Theme.Border))
        using (var fr2 = Theme.RoundedRect(frame, fh / 2f)) g.DrawPath(fpn, fr2);

        float cy = Height / 2f, x0 = TrackX0, x1 = TrackX1;
        float frac = _max > _min ? (float)(_value - _min) / (_max - _min) : 0;
        float kx = x0 + frac * (x1 - x0);

        // Size pictograms: a small photo tile on the left, a larger one on the right (what the slider scales to).
        Color glyph = Theme.Blend(Theme.Faint, Theme.TextCol, 0.30);
        DrawPhotoGlyph(g, 18f, cy, 11f, glyph);
        DrawPhotoGlyph(g, Width - 20f, cy, 15f, glyph);

        // Track + accent fill + knob
        using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.White, 0.14)))
        using (var tp = Theme.RoundedRect(new RectangleF(x0, cy - 2, x1 - x0, 4), 2)) g.FillPath(tb, tp);
        using (var fbk = new SolidBrush(Theme.Accent))
        using (var fp = Theme.RoundedRect(new RectangleF(x0, cy - 2, Math.Max(0.1f, kx - x0), 4), 2)) g.FillPath(fbk, fp);
        using (var ks = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(ks, kx - 6.5f, cy - 5.5f, 13, 13); // soft drop shadow
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, kx - 6.5f, cy - 6.5f, 13, 13);
        using (var kp = new Pen(Color.FromArgb(55, 0, 0, 0))) g.DrawEllipse(kp, kx - 6.5f, cy - 6.5f, 13, 13);
    }

    /// <summary>A tiny "photo" pictogram: a rounded picture frame with a sun and a mountain, scaled to
    /// <paramref name="size"/> — drawn small on the left and larger on the right to read as a size control.</summary>
    private static void DrawPhotoGlyph(Graphics g, float cx, float cy, float size, Color c)
    {
        var r = new RectangleF(cx - size / 2f, cy - size / 2f, size, size);
        using var pen = new Pen(c, Math.Max(1f, size * 0.10f));
        using (var fr = Theme.RoundedRect(r, Math.Max(1.5f, size * 0.18f))) g.DrawPath(pen, fr);
        using var b = new SolidBrush(c);
        // sun (top-left), mountain (bottom) — kept inside the frame
        float sun = size * 0.16f;
        g.FillEllipse(b, r.X + size * 0.24f - sun, r.Y + size * 0.30f - sun, sun * 2, sun * 2);
        var m = new[]
        {
            new PointF(r.X + size * 0.14f, r.Bottom - size * 0.18f),
            new PointF(r.X + size * 0.46f, r.Y + size * 0.50f),
            new PointF(r.Right - size * 0.12f, r.Bottom - size * 0.18f),
        };
        g.FillPolygon(b, m);
    }
}
