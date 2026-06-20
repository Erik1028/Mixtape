using System.Diagnostics;

namespace iPodCommander;

/// <summary>
/// A tiny UI-thread tween engine for Apple-like motion. One shared WinForms timer drives every active
/// <see cref="Tween"/>; each tween is Stopwatch-timed so it's frame-rate independent. Callbacks run on
/// the UI thread, so handlers may touch controls directly. Honours the user's "show animations" setting
/// (<see cref="MotionEnabled"/>) — when off, a tween jumps straight to its final frame.
/// </summary>
internal static class Anim
{
    private static readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 }; // ~66 fps
    private static readonly List<Tween> _active = new();

    /// <summary>Respect the OS "show animations in Windows" preference; can be forced off for tests.</summary>
    public static bool MotionEnabled { get; set; } = SystemInformation.UIEffectsEnabled;

    static Anim() { _timer.Tick += (_, _) => Tick(); }

    /// <summary>
    /// Run a tween: <paramref name="onTick"/> is called with an eased progress 0→1 each frame, and
    /// <paramref name="onDone"/> once at the end. Returns a handle you can <see cref="Tween.Cancel"/>
    /// (e.g. to restart). When motion is disabled the final frame + onDone fire immediately.
    /// </summary>
    public static Tween Run(double durationMs, Action<double> onTick, Action? onDone = null, Func<double, double>? ease = null)
    {
        var tw = new Tween { DurationMs = Math.Max(1, durationMs), Ease = ease ?? Easings.OutCubic, OnTick = onTick, OnDone = onDone };
        if (!MotionEnabled)
        {
            try { onTick(1); } catch { }
            try { onDone?.Invoke(); } catch { }
            tw.Done = true;
            return tw;
        }
        tw.Sw.Start();
        try { onTick(0); } catch { } // paint the first frame now so there's no flash before the first tick
        _active.Add(tw);
        if (!_timer.Enabled) _timer.Start();
        return tw;
    }

    private static void Tick()
    {
        // Iterate a snapshot count backwards so cancels/adds during callbacks are safe.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (i >= _active.Count) continue;
            var tw = _active[i];
            if (tw.Done) { _active.RemoveAt(i); continue; }
            double t = Math.Min(1.0, tw.Sw.Elapsed.TotalMilliseconds / tw.DurationMs);
            try { tw.OnTick(tw.Ease(t)); } catch { }
            if (t >= 1.0)
            {
                tw.Done = true;
                if (i < _active.Count && ReferenceEquals(_active[i], tw)) _active.RemoveAt(i);
                try { tw.OnDone?.Invoke(); } catch { }
            }
        }
        if (_active.Count == 0) _timer.Stop();
    }
}

/// <summary>A running (or cancellable) tween handle. <see cref="Cancel"/> stops it without firing onDone.</summary>
internal sealed class Tween
{
    internal readonly Stopwatch Sw = new();
    internal double DurationMs;
    internal Func<double, double> Ease = Easings.OutCubic;
    internal Action<double> OnTick = _ => { };
    internal Action? OnDone;
    internal bool Done;

    public bool IsRunning => !Done;
    public void Cancel() => Done = true;
}

/// <summary>Standard easing curves. Apple motion leans on ease-out (fast start, soft settle).</summary>
internal static class Easings
{
    public static double Linear(double t) => t;
    public static double OutQuad(double t) => 1 - (1 - t) * (1 - t);
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double OutQuint(double t) => 1 - Math.Pow(1 - t, 5);
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    /// <summary>Slight overshoot then settle — the iOS "snap into place" feel.</summary>
    public static double OutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}

/// <summary>The motion used when swapping the centre view. Change the one constant in MainForm to restyle every
/// view switch. Slide = horizontal push; Fade = cross-dissolve + small rise; Scale = zoom-in cross-dissolve.</summary>
internal enum ViewTransition { Slide, Fade, Scale }

/// <summary>
/// A throw-away overlay that animates between two snapshots (outgoing → incoming) over a region during a view
/// switch, then removes itself. The <see cref="ViewTransition"/> style picks the motion.
/// </summary>
internal sealed class TransitionPanel : Panel
{
    private readonly Bitmap _old, _new;
    private readonly ViewTransition _style;
    private float _t;
    private Action? _onDone;

    public TransitionPanel(Bitmap oldBmp, Bitmap newBmp, ViewTransition style)
    {
        _old = oldBmp; _new = newBmp; _style = style;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Enabled = false; // sit on top briefly without intercepting clicks
    }

    public void Start(Action onDone)
    {
        _onDone = onDone;
        double dur = _style == ViewTransition.Slide ? 360 : 320;
        Anim.Run(dur, v => { _t = (float)v; if (!IsDisposed) Invalidate(); },
            () => { var d = _onDone; _onDone = null; d?.Invoke(); }, Easings.OutCubic);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        switch (_style)
        {
            case ViewTransition.Slide:                          // old pushes off to the left, new rides in from the right
                g.DrawImage(_old, -Width * _t, 0, Width, Height);
                g.DrawImage(_new, Width * (1f - _t), 0, Width, Height);
                break;
            case ViewTransition.Scale:                          // new zooms up from 96% as it dissolves in over the old
                g.DrawImage(_old, 0, 0, Width, Height);
                float s = 0.96f + 0.04f * _t, w = Width * s, h = Height * s;
                Theme.DrawImageAlpha(g, _new, new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h), _t);
                break;
            default:                                            // Fade: incoming rises a touch as it dissolves in
                g.DrawImage(_old, 0, 0, Width, Height);
                Theme.DrawImageAlpha(g, _new, new RectangleF(0, 16f * (1f - _t), Width, Height), _t);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _old.Dispose(); _new.Dispose(); }
        base.Dispose(disposing);
    }
}
