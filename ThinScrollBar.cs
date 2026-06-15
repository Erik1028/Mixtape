using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A slim, dark, overlay scrollbar that drives a <see cref="DataGridView"/> (whose own light
/// native scrollbar is turned off). Row-based: maps the thumb to FirstDisplayedScrollingRowIndex.
/// Hidden when everything fits. Replaces the bright Windows scrollbar that breaks the dark look.
/// </summary>
internal sealed class ThinScrollBar : Control
{
    private DataGridView? _grid;
    private bool _dragging;
    private bool _hover;
    private int _dragStartY;
    private int _dragStartFirst;

    public ThinScrollBar()
    {
        Width = 12;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    public void Attach(DataGridView grid)
    {
        _grid = grid;
        grid.ScrollBars = ScrollBars.None;
        grid.RowsAdded += (_, _) => Invalidate();
        grid.RowsRemoved += (_, _) => Invalidate();
        grid.Scroll += (_, _) => Invalidate();
        grid.SizeChanged += (_, _) => Invalidate();
        grid.MouseWheel += (_, e) => ScrollRows(-Math.Sign(e.Delta) * 3);
    }

    private int Total => _grid?.RowCount ?? 0;
    private int Visible => _grid is null ? 0 : Math.Max(1, _grid.DisplayedRowCount(false));
    private int First => _grid is { RowCount: > 0 } g ? Math.Max(0, g.FirstDisplayedScrollingRowIndex) : 0;

    private void ScrollRows(int delta)
    {
        if (_grid is null || Total == 0) return;
        int max = Math.Max(0, Total - Visible);
        int next = Math.Min(max, Math.Max(0, First + delta));
        try { _grid.FirstDisplayedScrollingRowIndex = next; } catch { }
        Invalidate();
    }

    private (int Y, int H) Thumb()
    {
        int track = Math.Max(1, Height - 8);
        int h = Total <= Visible ? track : Math.Max(28, (int)(track * (double)Visible / Total));
        int max = Math.Max(0, Total - Visible);
        int y = 4 + (max == 0 ? 0 : (int)((track - h) * (double)First / max));
        return (y, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_grid is null || Total <= Visible) return; // nothing to scroll → no thumb
        var (y, h) = Thumb();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF((Width - 6) / 2f, y, 6, h);
        using var path = Theme.RoundedRect(r, 3);
        using var b = new SolidBrush(_dragging || _hover ? Color.FromArgb(112, 118, 126) : Color.FromArgb(72, 76, 82));
        e.Graphics.FillPath(b, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var (y, h) = Thumb();
        if (e.Y >= y && e.Y <= y + h) { _dragging = true; _dragStartY = e.Y; _dragStartFirst = First; }
        else ScrollRows(e.Y < y ? -Visible : Visible);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = true;
        if (_dragging && _grid is not null && Total > Visible)
        {
            var (_, h) = Thumb();
            int track = Math.Max(1, Height - 8);
            int max = Math.Max(0, Total - Visible);
            double perPx = max / (double)Math.Max(1, track - h);
            int next = (int)Math.Round(_dragStartFirst + (e.Y - _dragStartY) * perPx);
            try { _grid.FirstDisplayedScrollingRowIndex = Math.Min(max, Math.Max(0, next)); } catch { }
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}
