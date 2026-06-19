using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A fixed column-header strip for the song list. The body <see cref="DataGridView"/> has its own header
/// hidden (so the whole grid can pixel-scroll inside a viewport); this strip stays put and mirrors the
/// grid's columns 1:1 — it reads each column's on-screen rectangle from the grid, so widths/visibility
/// stay perfectly aligned with no duplicated layout. Clicking a column raises <see cref="SortRequested"/>;
/// the sorted column shows a ↑/↓ arrow. Dragging a column boundary resizes it (Fill columns trade weight
/// with a neighbour; fixed columns set their own width). Styling matches Theme.StyleGrid's header.
/// </summary>
internal sealed class TrackHeader : Control
{
    private readonly DataGridView _grid;
    public event Action<int>? SortRequested;   // body column index that was clicked

    private const int Grip = 5, MinCol = 44;
    private int _dragCol = -1;                  // column whose right boundary is being dragged
    private bool _didResize;                    // a drag just happened → suppress the click-to-sort

    public TrackHeader(DataGridView grid)
    {
        _grid = grid;
        Height = 34;   // = Theme grid ColumnHeadersHeight
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        _grid.ColumnWidthChanged += (_, _) => Invalidate();
    }

    /// <summary>Right-aligned columns (match SetupTrackGrid: PLAYS, ADDED and TIME are right-aligned).</summary>
    private static bool RightAligned(int colIndex) => colIndex is 5 or 6 or 7;

    private Rectangle ColRect(int i)
    {
        try { return _grid.Columns[i].Visible ? _grid.GetColumnDisplayRectangle(i, false) : Rectangle.Empty; }
        catch { return Rectangle.Empty; }
    }

    private static bool IsFill(DataGridViewColumn c) => c.InheritedAutoSizeMode == DataGridViewAutoSizeColumnMode.Fill;

    /// <summary>The next visible column after <paramref name="i"/>, or -1 if none (i is the last visible column).</summary>
    private int NextVisible(int i)
    {
        for (int j = i + 1; j < _grid.Columns.Count; j++)
            if (ColRect(j).Width > 0) return j;
        return -1;
    }

    /// <summary>The left column of the boundary within the grab zone of x — i.e. the column whose right edge is
    /// being hovered. Skips the artwork column (i=0) and the last visible column (its right edge is the panel edge,
    /// nothing to trade with). Returns -1 if no resizable boundary is near x.</summary>
    private int BoundaryAt(int x)
    {
        for (int i = 1; i < _grid.Columns.Count; i++)   // i=0 is artwork — its right edge isn't resizable
        {
            var r = ColRect(i);
            if (r.Width > 0 && Math.Abs(x - r.Right) <= Grip && NextVisible(i) >= 0) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _didResize = false;
        int c = BoundaryAt(e.X);
        if (c < 0) return;
        // Normalise every visible Fill column's weight to its current pixel width, so weight units == pixels
        // for the duration of the drag. That makes the resize math below land the boundary exactly under the
        // cursor instead of drifting (the old code mixed raw 32/24/30 weights with live pixel widths).
        for (int i = 0; i < _grid.Columns.Count; i++)
        {
            var col = _grid.Columns[i];
            if (ColRect(i).Width > 0 && IsFill(col)) col.FillWeight = Math.Max(1, col.Width);
        }
        _dragCol = c; Capture = true; Cursor = Cursors.VSplit;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragCol >= 0) { ResizeTo(e.X); return; }
        Cursor = BoundaryAt(e.X) >= 0 ? Cursors.VSplit : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragCol >= 0) { _dragCol = -1; Capture = false; Cursor = Cursors.Default; }
        base.OnMouseUp(e);
    }

    /// <summary>Resize the boundary to the right of <c>_dragCol</c> so it tracks the cursor x. Two cases:
    /// • both sides are Fill columns → trade pixel-normalised weight between them (the rest of the fill block
    ///   stays put); • the right side is a fixed column → fixed columns are anchored from the panel's right edge,
    ///   so its right edge is constant and we set its width to (rightEdge − x); the fill block reflows to fill the
    ///   remaining space. Either way the dragged boundary lands exactly at x.</summary>
    private void ResizeTo(int x)
    {
        int li = _dragCol, ri = NextVisible(li);
        if (ri < 0) return;
        var left = _grid.Columns[li];
        var right = _grid.Columns[ri];
        var lr = ColRect(li);
        var rr = ColRect(ri);
        if (lr.Width <= 0 || rr.Width <= 0) return;

        if (IsFill(left) && IsFill(right))
        {
            // Trade width between the two adjacent fill columns; their combined span is preserved.
            int span = lr.Width + rr.Width;
            int newLeft = Math.Clamp(x - lr.X, MinCol, Math.Max(MinCol, span - MinCol));
            left.FillWeight = newLeft;
            right.FillWeight = Math.Max(MinCol, span - newLeft);
        }
        else
        {
            // Right side is fixed (anchored from the right) — its right edge stays, so widen/narrow from the left.
            int maxW = Math.Max(MinCol, rr.Right - lr.X - MinCol);   // keep at least MinCol for the left column
            right.Width = Math.Clamp(rr.Right - x, MinCol, maxW);
        }
        _didResize = true;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _didResize || BoundaryAt(e.X) >= 0) { _didResize = false; return; }
        for (int i = 0; i < _grid.Columns.Count; i++)
        {
            var r = ColRect(i);
            if (r.Width > 0 && e.X >= r.X && e.X < r.X + r.Width) { SortRequested?.Invoke(i); return; }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var f = Theme.UiFont(8.5f, FontStyle.Bold);

        for (int i = 0; i < _grid.Columns.Count; i++)
        {
            var r = ColRect(i);
            if (r.Width <= 0) continue;
            bool right = RightAligned(i);
            int rp = i == 7 ? 18 : 14;   // TIME's cells sit 14px from the right edge (others 10px) → match the caption
            var pad = right ? new Rectangle(r.X + 4, 0, r.Width - rp, Height) : new Rectangle(r.X + 8, 0, r.Width - 12, Height);
            var flags = (right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _grid.Columns[i].HeaderText, f, pad, Theme.Faint, flags);
        }

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);   // hairline under the header
    }
}
