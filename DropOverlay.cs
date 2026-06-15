using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A "drop zone" card shown over the content area while the user drags files onto the window.
/// It is itself a drop target (AllowDrop), so it can cover the song grid without blocking the drop —
/// the form wires its DragOver/DragDrop back to the same handlers.
/// </summary>
internal sealed class DropOverlay : Panel
{
    private string _caption = "Drop to add";
    public string Caption
    {
        get => _caption;
        set { if (_caption != value) { _caption = value; if (IsHandleCreated) Invalidate(); } }
    }

    public DropOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        if (Width < 80 || Height < 80) return;

        int cw = Math.Min(440, Width - 64);
        int ch = Math.Min(210, Height - 48);
        if (cw < 160 || ch < 120)
        {
            // Tiny viewport — just a centred label.
            TextRenderer.DrawText(g, _caption, Theme.DisplayFont(13f, FontStyle.Bold), ClientRectangle, Theme.TextCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var card = new Rectangle((Width - cw) / 2, (Height - ch) / 2, cw, ch);
        using (var path = Theme.RoundedRect(card, 22))
        {
            using (var fill = new SolidBrush(Theme.Blend(Theme.PanelBg, Theme.Accent, 0.06))) g.FillPath(fill, path);
            using var pen = new Pen(Theme.Accent, 2.5f) { DashStyle = DashStyle.Dash, DashPattern = new[] { 4f, 3f } };
            g.DrawPath(pen, path);
        }

        // Accent circle with a down-arrow.
        int cx = card.Left + card.Width / 2;
        int cy = card.Top + 56;
        int rad = 26;
        using (var cb = new SolidBrush(Theme.Accent)) g.FillEllipse(cb, cx - rad, cy - rad, rad * 2, rad * 2);
        using (var ap = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(ap, cx, cy - 11, cx, cy + 12);
            g.DrawLine(ap, cx - 9, cy + 2, cx, cy + 12);
            g.DrawLine(ap, cx + 9, cy + 2, cx, cy + 12);
        }

        var title = new Rectangle(card.Left + 12, cy + rad + 10, card.Width - 24, 30);
        TextRenderer.DrawText(g, _caption, Theme.DisplayFont(15f, FontStyle.Bold), title, Theme.TextCol,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        var sub = new Rectangle(card.Left + 12, title.Bottom + 2, card.Width - 24, 22);
        TextRenderer.DrawText(g, "Release to add them", Theme.UiFont(9.5f), sub, Theme.Subtle,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }
}
