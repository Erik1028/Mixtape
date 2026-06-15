using Avalonia;
using Avalonia.Media;

namespace Mixtape.App;

/// <summary>
/// Live theming: mutates the shared SolidColorBrush resources in App.axaml (so every StaticResource
/// user updates instantly) and exposes the wallpaper colours for the window to repaint. Palettes match
/// the Windows app's variants + accent presets.
/// </summary>
internal static class AppTheme
{
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Teal", "#00C8AA"), ("Blue", "#2A82F6"), ("Indigo", "#786EF5"), ("Purple", "#B05CF6"),
        ("Pink", "#F55C8A"), ("Red", "#F0524C"), ("Orange", "#FF9538"), ("Green", "#36C86E"),
    };
    public static readonly string[] Variants = { "Graphite", "Midnight", "Carbon", "Mocha" };

    public static string CurrentAccent { get; private set; } = "Teal";
    public static string CurrentVariant { get; private set; } = "Graphite";

    /// <summary>Raised after Apply so the window can repaint its wallpaper gradient.</summary>
    public static event Action? Applied;

    public static Color ResolveAccent(string s)
    {
        if (!string.IsNullOrEmpty(s) && s[0] == '#') { try { return Color.Parse(s); } catch { } }
        foreach (var a in Accents) if (a.Name == s) return Color.Parse(a.Hex);
        return Color.Parse("#00C8AA");
    }

    public static void Apply(string accent, string variant)
    {
        CurrentAccent = accent;
        CurrentVariant = variant;
        var (bg, side, panel, hair) = Palette(variant);
        var acc = ResolveAccent(accent);
        SetBrush("AccentBrush", acc);
        SetBrush("AppBrush", bg);
        SetBrush("SidebarBrush", side);
        SetBrush("PanelBrush", panel);
        SetBrush("HairlineBrush", hair);

        // Also retint the Fluent system accent (DataGrid selection, sliders, toggles) so they match.
        SetColor("SystemAccentColor", acc);
        SetColor("SystemAccentColorDark1", Blend(acc, Colors.Black, 0.15));
        SetColor("SystemAccentColorDark2", Blend(acc, Colors.Black, 0.30));
        SetColor("SystemAccentColorDark3", Blend(acc, Colors.Black, 0.45));
        SetColor("SystemAccentColorLight1", Blend(acc, Colors.White, 0.15));
        SetColor("SystemAccentColorLight2", Blend(acc, Colors.White, 0.30));
        SetColor("SystemAccentColorLight3", Blend(acc, Colors.White, 0.45));

        Applied?.Invoke();
    }

    /// <summary>Wallpaper gradient stops + accent glow for the current theme.</summary>
    public static (Color top, Color mid, Color bot, Color glow) Wallpaper()
    {
        var (_, side, _, _) = Palette(CurrentVariant);
        var acc = ResolveAccent(CurrentAccent);
        var top = Blend(side, Colors.Black, 0.38);
        var mid = Blend(Blend(side, Colors.Black, 0.30), acc, 0.16);   // accent-tinted band
        var bot = Blend(side, Colors.Black, 0.50);
        var glow = Color.FromArgb(0x33, acc.R, acc.G, acc.B);
        return (top, mid, bot, glow);
    }

    private static (Color bg, Color side, Color panel, Color hair) Palette(string v) => v switch
    {
        "Midnight" => (Color.Parse("#13182B"), Color.Parse("#0D1020"), Color.Parse("#202840"), Color.Parse("#1E253C")),
        "Carbon"   => (Color.Parse("#0C0C0E"), Color.Parse("#030304"), Color.Parse("#1A1A1E"), Color.Parse("#1A1B1F")),
        "Mocha"    => (Color.Parse("#241D19"), Color.Parse("#1B1411"), Color.Parse("#362B25"), Color.Parse("#322923")),
        _          => (Color.Parse("#1D1E22"), Color.Parse("#16171A"), Color.Parse("#282B30"), Color.Parse("#2C2E33")),
    };

    private static void SetBrush(string key, Color c)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush b) b.Color = c;
    }

    private static void SetColor(string key, Color c)
    {
        if (Application.Current is { } app) app.Resources[key] = c;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
