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
    private bool _down;      // Classic only: a caption button visibly goes in when held
    private Tween? _tw;

    public WindowButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        MouseEnter += (_, _) => AnimHover(1f);
        MouseLeave += (_, _) => { AnimHover(0f); if (_down) { _down = false; Invalidate(); } };
        MouseDown += (_, me) => { if (me.Button == MouseButtons.Left && Theme.Classic) { _down = true; Invalidate(); } };
        MouseUp += (_, _) => { if (_down) { _down = false; Invalidate(); } };
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
        if (Theme.Classic) { PaintClassic(g); return; }
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

    /// <summary>
    /// The caption buttons of 1995: a small face-coloured square with a raised edge (sunken while held) and
    /// a hand-placed 1-pixel glyph. They are drawn, not scaled - at 16x14 every pixel of a minimise bar or a
    /// close cross is a decision, and anti-aliasing any of it would give the era away immediately.
    /// </summary>
    private void PaintClassic(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        var r = new Rectangle(0, 0, Width, Height);
        Theme.FaceBevel(g, r, raised: !_down);
        int dx = _down ? 1 : 0;
        int cx = Width / 2 + dx, cy = Height / 2 + dx;
        using var pen = new Pen(Theme.FaceDark);
        using var br = new SolidBrush(Theme.FaceDark);
        switch (Which)
        {
            case Kind.Minimize:
                g.FillRectangle(br, cx - 4, cy + 3, 7, 2);
                break;
            case Kind.Maximize when !_maximized:
                g.DrawRectangle(pen, cx - 5, cy - 5, 9, 9);
                g.DrawLine(pen, cx - 5, cy - 4, cx + 4, cy - 4);   // the thick caption line of a title bar
                break;
            case Kind.Maximize:                                     // restore: a small window in front of a larger one
                g.DrawRectangle(pen, cx - 2, cy - 5, 6, 6);
                g.DrawLine(pen, cx - 2, cy - 4, cx + 4, cy - 4);
                using (var face = new SolidBrush(Theme.Face)) g.FillRectangle(face, cx - 5, cy - 2, 7, 7);
                g.DrawRectangle(pen, cx - 5, cy - 2, 6, 6);
                g.DrawLine(pen, cx - 5, cy - 1, cx + 1, cy - 1);
                break;
            case Kind.Close:
                for (int i = 0; i < 6; i++)                          // a 6x6 cross drawn a pixel at a time, two pixels wide
                {
                    g.FillRectangle(br, cx - 3 + i, cy - 3 + i, 2, 1);
                    g.FillRectangle(br, cx + 2 - i, cy - 3 + i, 2, 1);
                }
                break;
            case Kind.MiniPlayer:                                    // a window with a smaller one inset in its corner
                g.DrawRectangle(pen, cx - 5, cy - 4, 9, 7);
                g.FillRectangle(br, cx, cy - 1, 4, 4);
                break;
        }
    }
}
