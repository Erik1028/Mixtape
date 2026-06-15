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
    private Panel? _viewport;       // panel mode: the clipping viewport
    private Panel? _content;        // panel mode: the (taller) content panel, scrolled via its Top
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
        grid.MouseWheel += (_, e) => SetFirst(First - Math.Sign(e.Delta) * 3);
    }

    /// <summary>Drive a manually-scrolled content panel (pixel-based): <paramref name="content"/> is taller
    /// than <paramref name="viewport"/> and scrolled by setting its Top. No native scrollbar exists.</summary>
    public void AttachScrollPanel(Panel viewport, Panel content)
    {
        _viewport = viewport; _content = content;
        viewport.ClientSizeChanged += (_, _) => Invalidate();
        content.SizeChanged += (_, _) => Invalidate();
    }

    private bool PanelMode => _viewport is not null && _content is not null;

    private int Total => PanelMode ? _content!.Height : (_grid?.RowCount ?? 0);
    private int Visible => PanelMode ? Math.Max(1, _viewport!.ClientSize.Height)
                                     : (_grid is null ? 0 : Math.Max(1, _grid.DisplayedRowCount(false)));
    private int First => PanelMode ? Math.Max(0, -_content!.Top)
                                   : (_grid is { RowCount: > 0 } g ? Math.Max(0, g.FirstDisplayedScrollingRowIndex) : 0);

    private void SetFirst(int v)
    {
        int max = Math.Max(0, Total - Visible);
        v = Math.Min(max, Math.Max(0, v));
        if (PanelMode) _content!.Top = -v;
        else if (_grid is not null) { try { _grid.FirstDisplayedScrollingRowIndex = v; } catch { } }
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
        if (Total <= Visible) return; // nothing to scroll → no thumb
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
        else SetFirst(First + (e.Y < y ? -Visible : Visible)); // page up/down
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = true;
        if (_dragging && Total > Visible)
        {
            var (_, h) = Thumb();
            int track = Math.Max(1, Height - 8);
            int max = Math.Max(0, Total - Visible);
            double perPx = max / (double)Math.Max(1, track - h);
            SetFirst((int)Math.Round(_dragStartFirst + (e.Y - _dragStartY) * perPx));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
}
