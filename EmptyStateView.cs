namespace iPodCommander;

/// <summary>The content card when nothing is connected: the quiet note mark, a line, a hint, and the two things
/// you can actually do here — open the music that is on this PC, or add a folder of it.</summary>
internal sealed class EmptyStateView : Panel
{
    public string Title = "", Hint = "";
    private readonly ThemedButton _primary = new() { Primary = true, Height = 32 };
    private readonly ThemedButton _secondary = new() { Height = 32 };
    public event Action? PrimaryClicked, SecondaryClicked;

    public EmptyStateView(string primaryText, string secondaryText)
    {
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _primary.Text = primaryText; _secondary.Text = secondaryText;
        _primary.Click += (_, _) => PrimaryClicked?.Invoke();
        _secondary.Click += (_, _) => SecondaryClicked?.Invoke();
        Controls.Add(_primary); Controls.Add(_secondary);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // DrawEmptyState stacks the mark (cy-74..cy-18), the title (cy-8..) and the hint (cy+40..cy+80); the pair sits under that.
        int cx = Width / 2, cy = Height / 2;
        int wP = Math.Max(120, _primary.NeededWidth + 12), wS = Math.Max(120, _secondary.NeededWidth + 12);
        const int gap = 10;
        int x = cx - (wP + gap + wS) / 2, y = cy + 96;
        _primary.SetBounds(x, y, wP, 32);
        _secondary.SetBounds(x + wP + gap, y, wS, 32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Bg);
        Theme.DrawEmptyState(e.Graphics, ClientRectangle, Title, Hint);
    }
}
