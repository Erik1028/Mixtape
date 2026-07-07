using Avalonia;
using Avalonia.Media;

namespace Mixtape.App;

/// <summary>
/// Live theming: mutates the shared SolidColorBrush resources in App.axaml (so every StaticResource
/// user updates instantly) and exposes the wallpaper colours for the window to repaint. Palettes and
/// every derived colour mirror the Windows app's Theme.cs formulas exactly (same blends, same tuples),
/// so the two apps render the same colours for the same accent + variant.
/// </summary>
internal static class AppTheme
{
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Teal", "#00C8AA"), ("Blue", "#2A82F6"), ("Indigo", "#786EF5"), ("Purple", "#B05CF6"),
        ("Pink", "#F55C8A"), ("Red", "#F0524C"), ("Orange", "#FF9538"), ("Green", "#36C86E"),
    };
    public static readonly string[] Variants = { "Graphite", "Midnight", "Carbon", "Mocha", "Forest", "Plum" };

    private static readonly Color ErrorCol = Color.FromRgb(255, 120, 110);   // Theme.ErrorCol

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
        var (bg, side, panel, hover, hair, border) = Palette(variant);
        var acc = ResolveAccent(accent);
        var accBright = Blend(acc, Colors.White, 0.28);                       // Theme.AccentBright (runtime)
        var onAcc = (0.299 * acc.R + 0.587 * acc.G + 0.114 * acc.B) > 130    // Theme.OnAccent
            ? Color.FromRgb(12, 16, 18) : Colors.White;
        var rowBg = Blend(panel, Colors.White, 0.05);                         // Theme.RowBg (raised chip fill)

        SetBrush("AccentBrush", acc);
        SetBrush("AccentBrightBrush", accBright);
        SetBrush("OnAccentBrush", onAcc);
        SetBrush("AppBrush", bg);
        SetBrush("SidebarBrush", side);
        SetBrush("PanelBrush", panel);
        SetBrush("RowHoverBrush", hover);
        SetBrush("HairlineBrush", hair);
        SetBrush("OutlineBrush", border);
        SetBrush("RowBgBrush", rowBg);

        // Buttons (ThemedButton chip formulas): secondary = RowBg + lightened border; primary = accent;
        // danger = RowBg with the error red text + a red-washed border.
        SetBrush("ChipBorderBrush", Blend(border, Colors.White, 0.12));
        SetBrush("PrimaryHoverBrush", Blend(acc, Colors.White, 0.14));
        SetBrush("PrimaryBorderBrush", Blend(acc, Colors.White, 0.12));
        SetBrush("ErrorBrush", ErrorCol);
        SetBrush("DangerBorderBrush", Blend(Blend(border, ErrorCol, 0.55), Colors.White, 0.12));
        SetBrush("DangerHoverBrush", Blend(rowBg, ErrorCol, 0.14));

        // Region colours (SearchBox / HeaderPanel / song list / sidebar paint formulas).
        SetBrush("SearchFillBrush", Blend(bg, Colors.Black, 0.30));           // recessed input surface
        SetBrush("KickerBrush", Blend(bg, acc, 0.85));                        // LIBRARY kicker
        SetBrush("SelRowBrush", Blend(bg, acc, 0.12));                        // selected row whisper tint
        SetBrush("RowDividerBrush", Blend(bg, Colors.White, 0.03));           // 1px row divider
        SetBrush("SidebarSelBrush", Color.FromArgb(48, acc.R, acc.G, acc.B)); // translucent teal wash pill
        SetBrush("SidebarHoverBrush", Blend(side, Colors.White, 0.06));

        // DataGrid (Fluent theme) selection colours — the whisper accent tint at full opacity.
        SetColor("DataGridRowSelectedBackgroundColor", Blend(bg, acc, 0.12));
        SetColor("DataGridRowSelectedUnfocusedBackgroundColor", Blend(bg, acc, 0.12));
        SetColor("DataGridRowSelectedHoveredBackgroundColor", Blend(bg, acc, 0.16));
        SetColor("DataGridRowSelectedHoveredUnfocusedBackgroundColor", Blend(bg, acc, 0.16));

        // Also retint the Fluent system accent (sliders, toggles, focus) so they match.
        SetColor("SystemAccentColor", acc);
        SetColor("SystemAccentColorDark1", Blend(acc, Colors.Black, 0.15));
        SetColor("SystemAccentColorDark2", Blend(acc, Colors.Black, 0.30));
        SetColor("SystemAccentColorDark3", Blend(acc, Colors.Black, 0.45));
        SetColor("SystemAccentColorLight1", Blend(acc, Colors.White, 0.15));
        SetColor("SystemAccentColorLight2", Blend(acc, Colors.White, 0.30));
        SetColor("SystemAccentColorLight3", Blend(acc, Colors.White, 0.45));

        Applied?.Invoke();
    }

    /// <summary>Wallpaper colours, mirroring Theme.PaintWallpaper: a 60° 4-stop gradient (positions
    /// 0 / 0.30 / 0.62 / 1) + an accent glow top-left + a hue-shifted second glow bottom-right.</summary>
    public static (Color a0, Color a1, Color mid, Color a3, Color glow1, Color glow2) Wallpaper()
    {
        var (_, side, _, _, _, _) = Palette(CurrentVariant);
        var acc = ResolveAccent(CurrentAccent);
        var a0 = Blend(side, Colors.Black, 0.38);                            // WallpaperTop
        var a1 = Blend(side, Colors.Black, 0.42);
        var mid = Blend(Blend(side, Colors.Black, 0.30), acc, 0.12);         // subtle accent-tinted band
        var a3 = Blend(side, Colors.Black, 0.52);
        var glow1 = Color.FromArgb(55, acc.R, acc.G, acc.B);
        var g2 = HsvToColor((AccentHue(acc) + 55) % 360, 0.55, 0.72);
        var glow2 = Color.FromArgb(40, g2.R, g2.G, g2.B);
        return (a0, a1, mid, a3, glow1, glow2);
    }

    /// <summary>The idle art-placeholder tile (now-playing bar, nothing playing): a neutral 60° gradient
    /// derived from the sidebar colour (Theme: Blend(SidebarBg,White,0.09) → Blend(SidebarBg,Black,0.06)).</summary>
    public static (Color top, Color bottom) IdleTile()
    {
        var (_, side, _, _, _, _) = Palette(CurrentVariant);
        return (Blend(side, Colors.White, 0.09), Blend(side, Colors.Black, 0.06));
    }

    /// <summary>The generated artwork tile colours for a title (Theme.MakeArt): a stable per-name hue in the
    /// 150°–280° teal→blue→violet band, painted as a 60° gradient HSV(h,.50,.56) → HSV(h+24,.60,.34).</summary>
    public static (Color top, Color bottom) ArtTile(string title)
    {
        int seed = StableHash(title);
        double hue = 150 + (seed % 360) / 360.0 * 130;
        return (HsvToColor(hue, 0.50, 0.56), HsvToColor((hue + 24) % 360, 0.60, 0.34));
    }

    /// <summary>ArtTile as a ready 60°-ish LinearGradientBrush (for row/header placeholder tiles).</summary>
    public static IBrush ArtTileBrush(string title)
    {
        var (top, bottom) = ArtTile(title);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0.866, RelativeUnit.Relative),   // 60° from the x-axis
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
    }

    // The Windows Theme.SetThemeVariant tuples, verbatim: (Bg, SidebarBg, PanelBg, RowHover, HairLine, Border).
    private static (Color bg, Color side, Color panel, Color hover, Color hair, Color border) Palette(string v) => v switch
    {
        "Midnight" => (Color.Parse("#13182B"), Color.Parse("#0D1020"), Color.Parse("#202840"), Color.Parse("#1C233A"), Color.Parse("#1E253C"), Color.Parse("#262F4C")),
        "Carbon"   => (Color.Parse("#0C0C0E"), Color.Parse("#030304"), Color.Parse("#1A1A1E"), Color.Parse("#18181C"), Color.Parse("#1A1B1F"), Color.Parse("#222328")),
        "Mocha"    => (Color.Parse("#241D19"), Color.Parse("#1B1411"), Color.Parse("#362B25"), Color.Parse("#322822"), Color.Parse("#322923"), Color.Parse("#3E322B")),
        "Forest"   => (Color.Parse("#14221C"), Color.Parse("#0D1813"), Color.Parse("#1F3128"), Color.Parse("#1B2B23"), Color.Parse("#1D2D25"), Color.Parse("#273A30")),
        "Plum"     => (Color.Parse("#211A2A"), Color.Parse("#181220"), Color.Parse("#30273C"), Color.Parse("#2B2336"), Color.Parse("#2D2538"), Color.Parse("#392F47")),
        _          => (Color.Parse("#1D1E22"), Color.Parse("#16171A"), Color.Parse("#282B30"), Color.Parse("#262A2E"), Color.Parse("#2C2E33"), Color.Parse("#2C2F31")),
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

    // ---- ports of the Windows Theme helpers (identical arithmetic, C# int-cast truncation) ----

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char ch in s) h = h * 31 + ch;
            return h & 0x7FFFFFFF;
        }
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static double AccentHue(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0, dl = max - min;
        if (dl < 0.001) return 180;
        double rr = c.R / 255.0, gg = c.G / 255.0, bb = c.B / 255.0, h;
        if (max == rr) h = (gg - bb) / dl % 6; else if (max == gg) h = (bb - rr) / dl + 2; else h = (rr - gg) / dl + 4;
        h *= 60; if (h < 0) h += 360; return h;
    }
}
