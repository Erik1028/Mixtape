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

        // Paint the parent's wallpaper, translated, so this button is seamless with the caption strip.
        if (Parent is { } p)
        {
            var st = g.Save();
            g.TranslateTransform(-Left, -Top);
            Theme.PaintWallpaper(g, p.ClientRectangle);
            g.Restore(st);
        }
        else g.Clear(Theme.WallpaperTop);

        if (h > 0.001f)
        {
            Color baseCol = Which == Kind.Close ? Color.FromArgb(232, 17, 35) : Color.FromArgb(255, 255, 255);
            int a = (int)((Which == Kind.Close ? 255 : 36) * h);
            using var hb = new SolidBrush(Color.FromArgb(a, baseCol));
            g.FillRectangle(hb, ClientRectangle);
        }

        Color stroke = Which == Kind.Close ? Theme.Blend(Color.FromArgb(218, 222, 226), Color.White, h) : Color.FromArgb(218, 222, 226);
        using var pen = new Pen(stroke, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width / 2f, cy = Height / 2f, s = 5f; // glyph half-size

        switch (Which)
        {
            case Kind.Minimize:
                g.DrawLine(pen, cx - s, cy, cx + s, cy);
                break;
            case Kind.Maximize when !_maximized:
                g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
                break;
            case Kind.Maximize: // restore: two offset squares
                float o = 2f;
                g.DrawRectangle(pen, cx - s + o, cy - s - o, s * 2 - o, s * 2 - o); // back square (top-right)
                using (var fillFront = new SolidBrush(h > 0.5f ? Color.FromArgb(70, 0, 0, 0) : Theme.WallpaperTop))
                    g.FillRectangle(fillFront, cx - s, cy - s + o, s * 2 - o, s * 2 - o);
                g.DrawRectangle(pen, cx - s, cy - s + o, s * 2 - o, s * 2 - o);          // front square (bottom-left)
                break;
            case Kind.Close:
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                break;
            case Kind.MiniPlayer: // picture-in-picture: a window with a smaller filled window in the corner — "mini player"
                var outer = new RectangleF(cx - s - 1, cy - s, (s + 1) * 2, s * 2);
                using (var op = Theme.RoundedRect(outer, 2f)) g.DrawPath(pen, op);
                float iw = outer.Width * 0.5f, ih = outer.Height * 0.54f;
                var inner = new RectangleF(outer.Right - iw - 1.4f, outer.Bottom - ih - 1.4f, iw, ih);
                using (var ib = new SolidBrush(stroke)) using (var ip = Theme.RoundedRect(inner, 1.6f)) g.FillPath(ib, ip);
                break;
        }
    }
}
