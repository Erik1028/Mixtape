namespace iPodCommander;

/// <summary>
/// The Windows 95 look's volume window - the little one 1995's taskbar speaker opened: "Volume" at the top, a vertical
/// trackbar with its tick marks down the side, "Mute" at the foot. It works the player's own volume and mute, so it
/// and the deck's slider always agree; clicking anywhere else closes it (the popover does that), as the tray's did.
/// Drag or click the trackbar, turn the wheel, or use the arrow, Page and Home/End keys; Esc closes.
/// </summary>
internal sealed class ClassicVolumePopup : FlyoutForm
{
    private readonly Func<double> _level;      // 0..1: where the slider sits (kept while muted, as 95's was)
    private readonly Func<bool> _muted;
    private readonly Action<double> _setLevel;
    private readonly Action _toggleMute;
    private readonly Font _font = Theme.UiFont(Theme.SzBody);
    private bool _drag;

    public ClassicVolumePopup(Func<double> level, Func<bool> muted, Action<double> setLevel, Action toggleMute)
    {
        _level = level; _muted = muted; _setLevel = setLevel; _toggleMute = toggleMute;
        Text = Loc.T("Volume");
        ClientSize = new Size(76, 160);
        BackColor = Theme.Face;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    private Rectangle TrackRect => new(ClientSize.Width / 2 - 12, 32, 24, ClientSize.Height - 32 - 44);
    private Rectangle MuteBox => new(12, ClientSize.Height - 27, 13, 13);
    private Rectangle MuteHit => Rectangle.FromLTRB(6, ClientSize.Height - 32, ClientSize.Width - 6, ClientSize.Height - 8);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Face);
        base.OnPaint(e);   // the raised edge of a 1995 popup
        TextRenderer.DrawText(g, Loc.T("Volume"), _font, new Rectangle(0, 9, ClientSize.Width, 16), Theme.TextCol,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        var t = TrackRect;
        using (var k = new Pen(Theme.FaceDark))
            for (int i = 0; i <= 10; i++)   // the ticks down the right, as a 95 trackbar drew them - longer at the ends and the middle
            {
                int y = t.Bottom - (int)Math.Round(i / 10.0 * t.Height);
                g.DrawLine(k, t.Right + 1, y, t.Right + (i % 5 == 0 ? 6 : 4), y);
            }
        Theme.ClassicTrackbar(g, t, _level(), vertical: true);
        Theme.ClassicCheckBox(g, MuteBox, _muted());
        TextRenderer.DrawText(g, Loc.T("Mute"), _font, Rectangle.FromLTRB(MuteBox.Right + 5, MuteBox.Y - 2, ClientSize.Width - 4, MuteBox.Bottom + 2), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (MuteHit.Contains(e.Location)) { _toggleMute(); Invalidate(); return; }
        if (Rectangle.Inflate(TrackRect, 8, 6).Contains(e.Location)) { _drag = true; SetFromY(e.Y); }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFromY(e.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Set(_level() + (e.Delta > 0 ? 0.05 : -0.05)); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = e.KeyCode switch { Keys.Up or Keys.Right => 0.02, Keys.Down or Keys.Left => -0.02, Keys.PageUp => 0.1, Keys.PageDown => -0.1, _ => 0 };
        if (step != 0) { Set(_level() + step); e.Handled = true; return; }
        if (e.KeyCode == Keys.Home) { Set(1); e.Handled = true; return; }
        if (e.KeyCode == Keys.End) { Set(0); e.Handled = true; return; }
        base.OnKeyDown(e);   // Esc closes
    }

    private void SetFromY(int y) { var t = TrackRect; Set((t.Bottom - y) / (double)Math.Max(1, t.Height)); }
    private void Set(double v) { _setLevel(Math.Clamp(v, 0, 1)); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
