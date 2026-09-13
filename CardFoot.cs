namespace iPodCommander;

/// <summary>The content card's bottom strip when the player lives in the top deck: a few px of card surface that
/// carve the card's bottom corners (anti-aliased, sampling the wallpaper) and give the last row a little floor.</summary>
internal sealed class CardFoot : Panel
{
    public const int H = 12;

    public CardFoot()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Height = H;
        Margin = Padding.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Bg);
        Theme.CarveCardCorners(e.Graphics, this, Theme.RadShell, false, false, true, true);
    }
}
