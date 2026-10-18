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

    // Where each view drew its tags in its last paint, with the colour under them: the reveal redraws exactly these,
    // straight onto the window. Repainting the whole page every frame of the reveal (what it did first) cost the UI
    // thread ~400 ms of every page switch - the transitions stuttered and clicks waited (measured: MIX_SWITCHBENCH).
    private sealed record Spot(string Text, int X, int Y, Color Bg);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, List<Spot>> Spots = new();

    /// <summary>A tag view starts a paint: forget where its tags were (they are recorded again as they are drawn).</summary>
    public static void BeginPaint(Control c) { if (Spots.TryGetValue(c, out var l)) l.Clear(); }

    /// <summary>Draw a page tag for <paramref name="c"/> on its <paramref name="bg"/>, and remember where, so the reveal
    /// can redraw just it.</summary>
    public static void Tag(Control c, Graphics g, string text, int x, int y, Color bg)
    {
        Theme.DrawCyberTag(g, text, x, y);
        Spots.GetOrCreateValue(c).Add(new Spot(text, x, y, bg));
    }

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
        _tw = Anim.Run(560, v => { TagT = (float)v; RedrawTags(); }, () => { _tw = null; TagT = 1f; RedrawTags(); }, Easings.OutCubic);
    }

    /// <summary>One frame of the reveal: each recorded tag drawn at the current phase into a small bitmap over its own
    /// background, and that blitted onto the window - a few hundred pixels, not the page.</summary>
    private static void RedrawTags()
    {
        Control[] live;
        lock (_targets)
        {
            live = _targets.Select(w => w.TryGetTarget(out var t) ? t : null).Where(t => t is { IsDisposed: false, Visible: true, IsHandleCreated: true }).ToArray()!;
        }
        foreach (var c in live)
        {
            if (!Spots.TryGetValue(c, out var spots) || spots.Count == 0) continue;
            if (GetUpdateRect(c.Handle, IntPtr.Zero, false)) continue;   // a paint is pending: it draws the tags at this phase itself (and knows where they are now)
            using var g = c.CreateGraphics();
            foreach (var t in spots.ToArray())
            {
                int w = Theme.CyberTagWidth(g, t.Text) + 2;
                using var bmp = new Bitmap(w, 17);
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.Clear(t.Bg);
                    Theme.DrawCyberTag(bg, t.Text, 0, 0);
                }
                g.DrawImageUnscaled(bmp, t.X, t.Y);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr hwnd, IntPtr rect, bool erase);

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
