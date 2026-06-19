using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The song list's DataGridView, re-hosted for PIXEL-smooth scrolling: its own column header is hidden
/// and it is sized to its FULL content height inside a clipping viewport, then scrolled by moving its
/// <see cref="Control.Top"/> (eased) — never by the grid's own row-based scroll. Because every row stays
/// laid out at a fixed y, all the grid's normal logic (hit-testing, GetRowDisplayRectangle, selection,
/// sorting, drag-reorder) keeps working unchanged; only the scroll mechanism differs. The themed
/// <c>ThinScrollBar</c> drives/repaints via the panel-scroll contract (it watches Top via LocationChanged).
/// </summary>
internal sealed class SmoothGrid : DataGridView
{
    private double _target;     // target Top while animating (<= 0)
    private Tween? _tw;
    private bool _suppressBlit; // true only while we move Top to scroll → tells WndProc to discard (not blit) old pixels

    public SmoothGrid()
    {
        DoubleBuffered = true;
        ScrollBars = ScrollBars.None;
    }

    private int RowH => Math.Max(1, RowTemplate.Height);
    private int ViewportH => Parent?.ClientSize.Height ?? Height;
    private int MaxScroll => Math.Max(0, Height - ViewportH);   // how far up the content can move

    /// <summary>Set the content's Top (clamped to the scrollable range).</summary>
    public void SetScrollTop(int top)
    {
        top = Math.Clamp(top, -MaxScroll, 0);
        if (Top == top) return;
        // Moving the control normally lets the OS BLIT the old pixels to the new position; at the (fractional)
        // Fill-column boundaries that blit can leave faint vertical seams AND, when the move and repaint don't
        // line up, a ghost sliver of a row — the "weird grid" / "stuck partial row" people see while scrolling.
        // Suppress the blit (see WndProc): the whole client is discarded and repainted fresh, so every presented
        // frame is a real, seam-free paint. Cheaper too — no wasted blit that we immediately overpaint.
        _suppressBlit = true;
        Top = top;        // LocationChanged → ThinScrollBar repaints
        _suppressBlit = false;
        Update();         // present the freshly painted frame now, so animated scrolling stays crisp
    }

    /// <summary>Nudge the scroll by a pixel delta (used for drag auto-scroll near the edges).</summary>
    public void ScrollByPixels(int dy) { _tw?.Cancel(); _tw = null; SetScrollTop(Top - dy); }

    /// <summary>Stop any in-flight wheel animation, so an external driver (the scrollbar) can't fight it.</summary>
    public void CancelAnim() { _tw?.Cancel(); _tw = null; }

    /// <summary>Scroll so row <paramref name="index"/> is fully visible (called on activate/play).</summary>
    public void EnsureRowVisible(int index)
    {
        if (index < 0 || MaxScroll == 0) return;
        int rowTop = index * RowH, rowBot = rowTop + RowH;
        int viewTop = -Top, viewBot = viewTop + ViewportH;
        if (rowTop < viewTop) SetScrollTop(-rowTop);
        else if (rowBot > viewBot) SetScrollTop(-(rowBot - ViewportH));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Do NOT call base — the grid never scrolls itself; we move the whole control, eased.
        if (MaxScroll <= 0) return;
        bool running = _tw is { IsRunning: true };
        double cur = running ? _target : Top;
        double dy = (e.Delta / 120.0) * RowH * 3;     // wheel up (delta>0) → Top toward 0 (content down); ~3 rows/notch
        _target = Math.Clamp(cur + dy, -MaxScroll, 0);
        if (!Anim.MotionEnabled) { SetScrollTop((int)Math.Round(_target)); return; }
        double from = Top;
        _tw?.Cancel();
        _tw = Anim.Run(190, v => SetScrollTop((int)Math.Round(from + (_target - from) * v)), () => _tw = null, Easings.OutCubic);
    }

    // ---- blit-free move: discard the client on a scroll move so it repaints fresh (no seams / ghost rows) ----
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOCOPYBITS = 0x0100;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WINDOWPOSCHANGING && _suppressBlit && m.LParam != IntPtr.Zero)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            wp.flags |= SWP_NOCOPYBITS;
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tw?.Cancel();
        base.Dispose(disposing);
    }
}
