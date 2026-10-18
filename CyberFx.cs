using System.Globalization;

namespace iPodCommander;

/// <summary>
/// The Cyber look's motion (after OrbitOverlay's Gfx/CyberFx.cs): a HUD that comes alive, not a light show. When a page
/// opens its caption tags are drawn in - the yellow body wiped in from the left, then the letters typed one by one with a
/// block cursor after the last; figures count up to their value. Short, small, nothing that flashes; with motion off
/// (Anim.MotionEnabled false - every render, and the Windows "show animations" setting) everything is at rest at once.
/// </summary>
internal static class CyberFx
{
    /// <summary>How far the current page's tags are drawn in: 0 = not yet, 1 = at rest.</summary>
    public static float TagT { get; private set; } = 1f;

    private static Tween? _tw;
    private static readonly List<WeakReference<Control>> _targets = new();

    /// <summary>A control that draws tags (Theme.DrawCyberTag) and must repaint while they are drawn in.</summary>
    public static void Watch(Control c)
    {
        lock (_targets)
        {
            _targets.RemoveAll(w => !w.TryGetTarget(out var t) || t.IsDisposed);
            _targets.Add(new WeakReference<Control>(c));
        }
    }

    /// <summary>A page opened: draw its tags in again.</summary>
    public static void PlayReveal()
    {
        _tw?.Cancel();
        if (!Theme.Cyber || !Anim.MotionEnabled) { TagT = 1f; return; }
        TagT = 0f;
        _tw = Anim.Run(560, v => { TagT = (float)v; Repaint(); }, () => { _tw = null; TagT = 1f; Repaint(); }, Easings.OutCubic);
    }

    private static void Repaint()
    {
        Control[] live;
        lock (_targets)
        {
            live = _targets.Select(w => w.TryGetTarget(out var t) ? t : null).Where(t => t is { IsDisposed: false, Visible: true }).ToArray()!;
        }
        foreach (var c in live) c.Invalidate();
    }

    /// <summary>The tag's two phases at <see cref="TagT"/>: the body's wipe, then the typing.</summary>
    public static (float Wipe, float Type) TagPhases(float t) =>
        (Math.Clamp(t / 0.35f, 0f, 1f), Math.Clamp((t - 0.25f) / 0.7f, 0f, 1f));

    // ---- numbers counting up (Orbit's CountUp): s with its first number at k of its value, in 12 steps ----
    private const int CountSteps = 12;
    private static readonly Dictionary<(string, int), string> Counts = new();

    public static string CountUp(string s, float k)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int step = (int)(Math.Clamp(k, 0f, 1f) * CountSteps);
        if (step >= CountSteps) return s;
        lock (Counts)
        {
            if (Counts.TryGetValue((s, step), out var c)) return c;
            if (Counts.Count > 512) Counts.Clear();
            return Counts[(s, step)] = Scale(s, step / (double)CountSteps);
        }
    }

    private static string Scale(string s, double k)
    {
        int i0 = 0;
        while (i0 < s.Length && !char.IsAsciiDigit(s[i0])) i0++;
        if (i0 == s.Length) return s;
        long v = 0;
        int dec = -1, i = i0;
        char sep = '.';
        for (; i < s.Length; i++)
        {
            char ch = s[i];
            if (char.IsAsciiDigit(ch)) { v = v * 10 + (ch - '0'); if (dec >= 0) dec++; }
            else if ((ch == '.' || ch == ',') && dec < 0 && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) { dec = 0; sep = ch; }
            else break;
            if (v > 1_000_000_000) return s;
        }
        long n = (long)Math.Round(v * k);
        string num;
        if (dec <= 0) num = n.ToString(CultureInfo.InvariantCulture);
        else
        {
            long p = 1;
            for (int d = 0; d < dec; d++) p *= 10;
            num = (n / p).ToString(CultureInfo.InvariantCulture) + sep + (n % p).ToString(CultureInfo.InvariantCulture).PadLeft(dec, '0');
        }
        return s[..i0] + num + s[i..];
    }
}
