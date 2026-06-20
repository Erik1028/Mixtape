using System.Diagnostics;

namespace iPodCommander;

/// <summary>
/// The song list's DataGridView, re-hosted for PIXEL-smooth scrolling: its own column header is hidden
/// and it is sized to its FULL content height inside a clipping viewport, then scrolled by moving its
/// <see cref="Control.Top"/> — never by the grid's own row-based scroll. Because every row stays laid out
/// at a fixed y, all the grid's normal logic (hit-testing, GetRowDisplayRectangle, selection, sorting,
/// drag-reorder) keeps working unchanged; only the scroll mechanism differs. The themed
/// <c>ThinScrollBar</c> drives/repaints via the panel-scroll contract (it watches Top via LocationChanged).
///
/// Moving <see cref="Control.Top"/> lets Windows BLIT the existing pixels to the new position and repaint
/// only the newly-exposed strip — far cheaper than a full-viewport repaint every frame. The faint column
/// seams that earlier forced a full no-blit repaint are gone now that each row fills its full-width
/// background before the cells paint (MainForm.OnRowPrePaint), so the blitted content is already seam-free.
/// </summary>
internal sealed class SmoothGrid : DataGridView
{
    public SmoothGrid()
    {
        DoubleBuffered = true;
        ScrollBars = ScrollBars.None;
        _scrollTimer.Tick += (_, _) => FollowTick();
    }

    /// <summary>Selected row indices captured just BEFORE a left mouse-down collapses a multi-selection — so a
    /// drag started on one of several selected rows can still carry the whole prior selection.</summary>
    public readonly List<int> PreClickSelection = new();

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            PreClickSelection.Clear();
            foreach (DataGridViewRow r in SelectedRows) PreClickSelection.Add(r.Index);
        }
        base.OnMouseDown(e);   // raises the MouseDown event handlers AFTER the (now-snapshotted) selection collapses
    }

    private int RowH => Math.Max(1, RowTemplate.Height);
    private int ViewportH => Parent?.ClientSize.Height ?? Height;
    private int MaxScroll => Math.Max(0, Height - ViewportH);   // how far up the content can move

    /// <summary>Set the content's Top (clamped). Windows blits + repaints only the exposed strip; the
    /// LocationChanged it raises drives the ThinScrollBar.</summary>
    public void SetScrollTop(int top)
    {
        top = Math.Clamp(top, -MaxScroll, 0);
        if (Top == top) return;
        Top = top;
    }

    // ---- inertial wheel scroll ----
    // An accumulating target that Top chases with a frame-rate-independent exponential follower. The old
    // approach restarted a fresh fixed-duration tween on every wheel notch, so a fast spin crawled — each
    // notch reset the glide to the barely-moved current position. A single continuous follower lets rapid
    // notches accumulate, so fast scrolling actually scrolls fast.
    private readonly System.Windows.Forms.Timer _scrollTimer = new() { Interval = 15 };
    private readonly Stopwatch _sw = new();
    private double _targetTop;     // destination Top (<= 0)
    private long _lastMs;
    private const double Tau = 80.0;   // follower time constant (ms) — smaller = snappier catch-up

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Do NOT call base — the grid never scrolls itself; we move the whole control.
        if (MaxScroll <= 0) return;
        double cur = _scrollTimer.Enabled ? _targetTop : Top;
        double dy = (e.Delta / 120.0) * RowH * 3;     // wheel up (delta>0) → Top toward 0 (content down); ~3 rows/notch
        _targetTop = Math.Clamp(cur + dy, -MaxScroll, 0);
        if (!Anim.MotionEnabled) { StopFollow(); SetScrollTop((int)Math.Round(_targetTop)); return; }
        StartFollow();
    }

    private void StartFollow()
    {
        if (_scrollTimer.Enabled) return;
        _sw.Restart(); _lastMs = 0;
        _scrollTimer.Start();
    }
    private void StopFollow() => _scrollTimer.Stop();

    private void FollowTick()
    {
        long now = _sw.ElapsedMilliseconds;
        double dt = Math.Clamp(now - _lastMs, 1, 64); _lastMs = now;
        double k = 1 - Math.Exp(-dt / Tau);                 // fraction of the remaining gap to close this frame
        _targetTop = Math.Clamp(_targetTop, -MaxScroll, 0); // window may have resized mid-glide
        double next = Top + (_targetTop - Top) * k;
        if (Math.Abs(_targetTop - next) < 0.5) { SetScrollTop((int)Math.Round(_targetTop)); StopFollow(); return; }
        SetScrollTop((int)Math.Round(next));
    }

    /// <summary>Nudge the scroll by a pixel delta (used for drag auto-scroll near the edges).</summary>
    public void ScrollByPixels(int dy) { StopFollow(); SetScrollTop(Top - dy); }

    /// <summary>Stop any in-flight wheel glide, so an external driver (the scrollbar) can't fight it.</summary>
    public void CancelAnim() => StopFollow();

    /// <summary>Scroll so row <paramref name="index"/> is fully visible (called on activate/play).</summary>
    public void EnsureRowVisible(int index)
    {
        if (index < 0 || MaxScroll == 0) return;
        int rowTop = index * RowH, rowBot = rowTop + RowH;
        int viewTop = -Top, viewBot = viewTop + ViewportH;
        if (rowTop < viewTop) { StopFollow(); SetScrollTop(-rowTop); }
        else if (rowBot > viewBot) { StopFollow(); SetScrollTop(-(rowBot - ViewportH)); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _scrollTimer.Stop(); _scrollTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
