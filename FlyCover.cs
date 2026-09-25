using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The cover you clicked, flying to where it lands. Opening an album used to cross-dissolve the whole content
/// area, which tells you the page changed but not that THIS cover became that page; carrying the picture from
/// the tile to the header says it in one move. It is a throwaway control on the shell: it owns its bitmap,
/// paints nothing but that, and removes itself when it arrives.
/// </summary>
internal sealed class FlyCover : Control
{
    private readonly Bitmap _img;
    private float _alpha = 1f;
    private Tween? _tw;

    private FlyCover(Bitmap img, Rectangle start)
    {
        _img = img;
        Bounds = start;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Enabled = false;   // never eats a click on its way across
    }

    /// <summary>Send <paramref name="img"/> from <paramref name="start"/> to <paramref name="end"/> on
    /// <paramref name="host"/> (both rectangles in the host's coordinates). Takes ownership of the bitmap.</summary>
    public static void Fly(Control host, Bitmap img, Rectangle start, Rectangle end, int ms = 320)
    {
        if (!Anim.MotionEnabled || host.IsDisposed) { img.Dispose(); return; }
        var f = new FlyCover(img, start);
        host.Controls.Add(f);
        f.BringToFront();
        f._tw = Anim.Run(ms, v =>
        {
            if (f.IsDisposed) return;
            f.Bounds = new Rectangle(
                (int)Math.Round(start.X + (end.X - start.X) * v),
                (int)Math.Round(start.Y + (end.Y - start.Y) * v),
                (int)Math.Round(start.Width + (end.Width - start.Width) * v),
                (int)Math.Round(start.Height + (end.Height - start.Height) * v));
            // It hands over to the header's own art at the end instead of vanishing on arrival.
            f._alpha = v < 0.72 ? 1f : (float)(1 - (v - 0.72) / 0.28);
            f.Invalidate();
        }, () =>
        {
            if (f.IsDisposed) return;
            host.Controls.Remove(f);
            f.Dispose();
        }, Easings.InOutCubic);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Outside the rounded corners the shell has to show through, so the wallpaper is blitted first - the
        // same thing every other floating piece of chrome in this app does.
        if (Parent is WallpaperPanel wp && wp.Wallpaper is { } wall) Theme.BlitExact(g, wall, Bounds);
        else g.Clear(Parent?.BackColor ?? Theme.Bg);
        var r = new RectangleF(0, 0, Width, Height);
        using var clip = Theme.RoundedRect(r, Width * Theme.TileFrac);
        var saved = g.Clip;
        g.SetClip(clip);
        if (_alpha >= 0.999f) g.DrawImage(_img, r);
        else Theme.DrawImageAlpha(g, _img, r, _alpha);
        g.Clip = saved;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); _img.Dispose(); }
        base.Dispose(disposing);
    }
}
