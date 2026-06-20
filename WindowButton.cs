using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A custom window-control button (minimize / maximize-restore / close) for the borderless shell.
/// It paints the same wallpaper slice behind itself so it melts into the caption strip, draws a crisp
/// vector glyph, and shows a hover highlight (red for Close, Windows-style).
/// </summary>
internal sealed class WindowButton : Control
{
    public enum Kind { Minimize, Maximize, Close, MiniPlayer }

    public Kind Which { get; init; }
    private bool _maximized;
    public bool Maximized { get => _maximized; set { if (_maximized == value) return; _maximized = value; Invalidate(); } }
    private float _hoverT;   // 0→1 hover highlight
    private bool _painted;
    private Tween? _tw;

    public WindowButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        MouseEnter += (_, _) => AnimHover(1f);
        MouseLeave += (_, _) => AnimHover(0f);
    }

    private void AnimHover(float to)
    {
        if (!_painted || !Anim.MotionEnabled) { _hoverT = to; Invalidate(); return; }
        _tw?.Cancel();
        float from = _hoverT;
        _tw = Anim.Run(110, v => { _hoverT = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Math.Clamp(_hoverT, 0f, 1f);

        // Seamless background: over the wallpaper caption strip paint the wallpaper (translated); when hosted
        // inside a solid card (the content header now) just fill the card's background colour.
        if (Parent is WallpaperPanel wp)
        {
            var st = g.Save();
            g.TranslateTransform(-Left, -Top);
            Theme.PaintWallpaper(g, wp.ClientRectangle);
            g.Restore(st);
        }
        else g.Clear(Parent?.BackColor ?? Theme.Bg);

        if (h > 0.001f)
        {
            Color baseCol = Which == Kind.Close ? Color.FromArgb(232, 17, 35) : Color.FromArgb(255, 255, 255);
            int a = (int)((Which == Kind.Close ? 255 : 36) * h);
            // A soft ROUNDED highlight inset from the cell edges (was a hard full-cell rectangle → too square).
            var hr = new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f);
            using var hb = new SolidBrush(Color.FromArgb(a, baseCol));
            using var hp = Theme.RoundedRect(hr, 7f);
            g.FillPath(hb, hp);
        }

        Color stroke = Which == Kind.Close ? Theme.Blend(Color.FromArgb(218, 222, 226), Color.White, h) : Color.FromArgb(218, 222, 226);
        using var pen = new Pen(stroke, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = Width / 2f, cy = Height / 2f, s = 5.5f, rad = 3.2f; // glyph half-size + corner radius

        switch (Which)
        {
            case Kind.Minimize:
                g.DrawLine(pen, cx - s, cy, cx + s, cy);
                break;
            case Kind.Maximize when !_maximized:
                using (var mp = Theme.RoundedRect(new RectangleF(cx - s, cy - s, s * 2, s * 2), rad)) g.DrawPath(pen, mp);
                break;
            case Kind.Maximize: // restore: two offset rounded squares
                float o = 2.2f, sq = s * 2 - o, rr = rad * 0.8f;
                using (var back = Theme.RoundedRect(new RectangleF(cx - s + o, cy - s - o, sq, sq), rr)) g.DrawPath(pen, back); // back (top-right)
                var front = new RectangleF(cx - s, cy - s + o, sq, sq);
                using (var fillFront = new SolidBrush(h > 0.5f ? Color.FromArgb(70, 0, 0, 0) : Theme.WallpaperTop))
                using (var fp = Theme.RoundedRect(front, rr)) g.FillPath(fillFront, fp);
                using (var fp2 = Theme.RoundedRect(front, rr)) g.DrawPath(pen, fp2);                                           // front (bottom-left)
                break;
            case Kind.Close:
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                break;
            case Kind.MiniPlayer: // picture-in-picture: a window with a smaller filled window in the corner — "mini player"
                var outer = new RectangleF(cx - s - 1, cy - s, (s + 1) * 2, s * 2);
                using (var op = Theme.RoundedRect(outer, rad)) g.DrawPath(pen, op);
                float iw = outer.Width * 0.5f, ih = outer.Height * 0.54f, inset = 2f;
                var inner = new RectangleF(outer.Right - iw - inset, outer.Bottom - ih - inset, iw, ih);
                using (var ib = new SolidBrush(stroke)) using (var ip = Theme.RoundedRect(inner, 1.8f)) g.FillPath(ib, ip);
                break;
        }
    }
}
