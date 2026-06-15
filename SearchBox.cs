using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A rounded, themed search field with a magnifier glyph and a clear (×) button.</summary>
internal sealed class SearchBox : Panel
{
    public event Action<string>? Changed;
    private readonly TextBox _tb;
    private bool _clearHover;

    public SearchBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Height = 34;

        _tb = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Blend(Theme.Bg, Color.Black, 0.30),
            ForeColor = Theme.TextCol,
            Font = Theme.UiFont(10f),
            PlaceholderText = "Search songs, artists, albums…",
        };
        _tb.TextChanged += (_, _) => { Changed?.Invoke(_tb.Text); Invalidate(); };
        Controls.Add(_tb);

        MouseMove += (_, e) => { bool h = ClearRect.Contains(e.Location) && _tb.Text.Length > 0; if (h != _clearHover) { _clearHover = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_clearHover) { _clearHover = false; Invalidate(); } };
        MouseClick += (_, e) => { if (_tb.Text.Length > 0 && ClearRect.Contains(e.Location)) { _tb.Clear(); _tb.Focus(); } };
    }

    public string Query => _tb.Text;
    public void ClearQuery() => _tb.Clear();

    private Rectangle ClearRect => new(Width - 26, (Height - 18) / 2, 18, 18);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_tb is null) return; // OnResize can fire from the ctor's Height set, before _tb exists
        int th = _tb.PreferredHeight;
        _tb.SetBounds(34, (Height - th) / 2, Width - 34 - 30, th);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        using (var p = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), (Height - 1) / 2f))
        {
            using (var b = new SolidBrush(Theme.Blend(Theme.Bg, Color.Black, 0.30))) g.FillPath(b, p); // recessed input surface
            using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
        }

        // magnifier
        using (var pen = new Pen(Theme.Faint, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            int cx = 14, cy = Height / 2, r = 5;
            g.DrawEllipse(pen, cx - r, cy - r - 1, r * 2, r * 2);
            g.DrawLine(pen, cx + r - 1, cy + r - 2, cx + r + 3, cy + r + 2);
        }
        // clear ×
        if (_tb.Text.Length > 0)
        {
            var cr = ClearRect;
            if (_clearHover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(cr, cr.Width / 2f); g.FillPath(hb, hp); }
            using var xpen = new Pen(_clearHover ? Theme.TextCol : Theme.Faint, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int m = 5;
            g.DrawLine(xpen, cr.Left + m, cr.Top + m, cr.Right - m, cr.Bottom - m);
            g.DrawLine(xpen, cr.Right - m, cr.Top + m, cr.Left + m, cr.Bottom - m);
        }
    }
}
