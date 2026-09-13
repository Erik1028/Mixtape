using System.Diagnostics;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A repaint clock that keeps up with the display, for views that animate continuously.
///
/// A WinForms timer cannot do this, and the numbers are not close. WM_TIMER is a low-priority
/// message delivered only when the queue is empty, and it is quantised to the system tick, so a
/// timer asking for 16 ms delivers repaints like this (measured on this machine, time between
/// actual OnPaint calls over three seconds):
///
///     WinForms Timer, Interval = 16      40 fps   median 27.1   p90 31.4   p99 32.2 ms   68% over 20 ms
///     threading.Timer 8 ms + Invoke     125 fps   median  8.1   p90  8.7   p99  9.1 ms    0% over 20 ms
///     DwmFlush, display-paced           180 fps   median  5.5   p90  6.7   p99  7.6 ms    0% over 20 ms
///
/// timeBeginPeriod(1) does NOT help: it was measured at 27 ms median with and without, because
/// since Windows 10 2004 the multimedia period no longer drives WM_TIMER delivery. Two thirds of
/// frames landing over 20 ms is what a continuously moving view looks like when it judders.
///
/// So this waits on the compositor instead. DwmFlush blocks until the next vertical blank, which
/// is what makes the motion match the panel rather than beat against it. It falls back to a plain
/// threading timer if DWM is not composing (a remote session, composition off), where DwmFlush
/// returns at once and a naive loop would spin a core.
///
/// The clock owns no state beyond the pacing: it simply asks the control to repaint, and never
/// lets more than one request be in flight, so a slow frame cannot pile up a backlog of invokes.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);

    private readonly Control _target;
    private readonly Action _tick;
    private Thread? _vsync;
    private System.Threading.Timer? _fallback;
    private volatile bool _run;
    private int _pending;          // 1 while a repaint request is on its way to the UI thread (or being painted)
    private long _tickAt;          // Stopwatch ticks when the last frame's paint began
    private double _paintMs;       // how long that paint took

    /// <param name="target">The control to repaint; also the thread to marshal onto.</param>
    public FrameClock(Control target)
    {
        _target = target;
        _tick = () =>
        {
            // Paint NOW, inside the posted message, and only then allow the next request. The old tick cleared the
            // flag first and merely invalidated: on a display faster than the paint (a 100 Hz panel, a ~8 ms frame)
            // the next request was already queued while the previous frame was still painting, and since a posted
            // message outranks input and WM_TIMER in the message queue, the UI thread did nothing but paint - the
            // intro tween never ticked (the stage stayed at its first, blank frame), clicks were not read: a freeze.
            try
            {
                if (_run && !_target.IsDisposed && _target.Visible)
                {
                    _tickAt = Stopwatch.GetTimestamp();
                    _target.Invalidate();
                    _target.Update();
                    _paintMs = (Stopwatch.GetTimestamp() - _tickAt) * 1000.0 / Stopwatch.Frequency;
                }
            }
            finally { Volatile.Write(ref _pending, 0); }
        };
    }

    public bool Running => _run;

    public void Start()
    {
        if (_run) return;
        _run = true;
        if (Composing())
        {
            _vsync = new Thread(Pump) { IsBackground = true, Name = "frame-clock" };
            _vsync.Start();
        }
        else StartFallback();
    }

    public void Stop()
    {
        _run = false;
        _fallback?.Dispose();
        _fallback = null;
        _vsync = null;             // it is a background thread and checks _run every frame
    }

    private static bool Composing()
    {
        try { return DwmIsCompositionEnabled(out bool on) == 0 && on; }
        catch { return false; }
    }

    private void Pump()
    {
        var sw = Stopwatch.StartNew();
        int instant = 0;
        while (_run)
        {
            long before = sw.ElapsedTicks;
            try { if (DwmFlush() != 0) { Fall(); return; } }
            catch { Fall(); return; }
            // DwmFlush returns immediately when there is nothing to wait for -- the window is
            // occluded, or composition stopped under us. Left alone that turns this into a busy
            // loop on a whole core, so count the instant returns and hand over if they persist.
            double waited = (sw.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency;
            if (waited < 1.0) { if (++instant > 240) { Fall(); return; } }
            else instant = 0;
            Request();
        }
    }

    private void Fall()
    {
        if (!_run) return;
        try { _target.BeginInvoke(new Action(StartFallback)); } catch { }
    }

    private void StartFallback()
    {
        if (!_run || _fallback is not null) return;
        // 8 ms, not 16: the point is to be finer than the frame, so a repaint is never waiting on
        // the next tick. Measured at 125 fps with nothing over 20 ms.
        _fallback = new System.Threading.Timer(_ => Request(), null, 0, 8);
    }

    private void Request()
    {
        if (!_run) return;
        // Leave the UI thread real room between frames: never ask for the next one until the last paint's own
        // duration has passed again (so painting can take at most half the thread), and never sooner than 8 ms.
        double since = (Stopwatch.GetTimestamp() - Volatile.Read(ref _tickAt)) * 1000.0 / Stopwatch.Frequency;
        if (since < Math.Max(8.0, _paintMs)) return;
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;   // one in flight is enough
        var t = _target;
        if (t.IsDisposed || !t.IsHandleCreated) { Volatile.Write(ref _pending, 0); return; }
        try { t.BeginInvoke(_tick); }
        catch { Volatile.Write(ref _pending, 0); }                 // the control went away mid-flight
    }

    public void Dispose() => Stop();
}
