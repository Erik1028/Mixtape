using System.Drawing;

namespace iPodCommander;

/// <summary>
/// Resolves an iPod's real body colour. Each colour variant shipped under its own model number
/// (e.g. a green mini is a different M-number than a silver one), so when we have the device's
/// model number we map it to the exact Apple colour; otherwise we fall back to the generation's
/// most-iconic default. Colours are muted anodized-aluminium / plastic tones (sourced from
/// everymac.com model-number tables), not pure RGB.
/// </summary>
internal static class IpodColors
{
    private static readonly Color White = Color.FromArgb(242, 241, 236);
    private static readonly Color Silver = Color.FromArgb(208, 210, 212);
    private static Color Hex(string h) => ColorTranslator.FromHtml(h);

    /// <summary>Exact model number (normalized core, e.g. "M9807") → real body colour.</summary>
    private static readonly Dictionary<string, Color> ByModel = new(StringComparer.OrdinalIgnoreCase);

    static IpodColors()
    {
        void M(string hex, params string[] models) { var c = Hex(hex); foreach (var m in models) ByModel[m] = c; }

        // iPod mini 1G
        M("#CDCFCB", "M9160"); M("#D8C9A5", "M9437"); M("#E7B8C3", "M9435"); M("#A9C3D5", "M9436"); M("#BFCDA0", "M9434");
        // iPod mini 2G  (the user's M9807 is Green)
        M("#D2D3CF", "M9800", "M9801"); M("#92B2CE", "M9802", "M9803"); M("#E0A8BE", "M9804", "M9805"); M("#AEC487", "M9806", "M9807");

        // iPod nano 1G
        M("#F2F1EC", "MA350", "MA004", "MA005"); M("#1D1D1F", "MA352", "MA099", "MA107");
        // iPod nano 2G
        M("#D2D3D0", "MA477", "MA426"); M("#3E73AE", "MA428"); M("#7DA84F", "MA487"); M("#E59AB0", "MA489"); M("#2A2A2C", "MA497"); M("#B6201F", "MA725", "MA899");
        // iPod nano 3G
        M("#CFD1CD", "MA978", "MA980"); M("#8FBDD2", "MB249"); M("#9CC368", "MB253"); M("#262629", "MB261"); M("#E79CB4", "MB453"); M("#B6201F", "MB257");
        // iPod nano 4G
        M("#D0D2D4", "MB598", "MB903"); M("#28282A", "MB754", "MB918"); M("#7E6BA8", "MB739", "MB909"); M("#4E84C4", "MB732", "MB905");
        M("#86B94B", "MB745", "MB913"); M("#E3CB4E", "MB748", "MB915"); M("#E78A3C", "MB742", "MB911"); M("#E892AE", "MB735", "MB907"); M("#B81C22", "MB751", "MB917");
        // iPod nano 5G
        M("#D0D2D4", "MC027", "MC060"); M("#28282A", "MC031", "MC062"); M("#7E6BA8", "MC034", "MC064"); M("#4E84C4", "MC037", "MC066");
        M("#86B94B", "MC040", "MC068"); M("#E3CB4E", "MC043", "MC070"); M("#E78A3C", "MC046", "MC072"); M("#E892AE", "MC050", "MC075"); M("#B81C22", "MC049", "MC074");
        // iPod nano 6G
        M("#D0D2D4", "MC525", "MC526"); M("#5E6064", "MC688", "MC694"); M("#4E84C4", "MC689", "MC695"); M("#86B94B", "MC690", "MC696");
        M("#E78A3C", "MC691", "MC697"); M("#E892AE", "MC692", "MC698"); M("#B81C22", "MC693", "MC699");
        // iPod nano 7G (2012 / 2013 / 2015)
        M("#D4D6D8", "MD480", "MKN22"); M("#3A3B3E", "MD481"); M("#8A6FB0", "MD479"); M("#EC9BB6", "MD475"); M("#ECD24B", "MD476");
        M("#8FC04A", "MD478"); M("#4E9BD6", "MD477", "MKN02"); M("#C41E25", "MD744", "MKN72"); M("#4A4B4E", "ME971", "MKN52");
        M("#D9C2A0", "MKMX2"); M("#E8638F", "MKMV2");

        // iPod shuffle 2G
        M("#D6D6D2", "MA564"); M("#6FA0C8", "MA949"); M("#9CC15A", "MA951"); M("#E68A3A", "MA953"); M("#E3A4BB", "MA947"); M("#B6201F", "MB233");
        // iPod shuffle 3G
        M("#CFCFCB", "MB867", "MC306"); M("#2A2A2C", "MC323"); M("#86B84E", "MC307", "MC381"); M("#4F8FC4", "MC328", "MC384"); M("#E58AAE", "MC331", "MC387");
        // iPod shuffle 4G
        M("#CFCFCB", "MC584", "MD778"); M("#E58AAE", "MC585", "MD773"); M("#55595C", "MD779"); M("#E4D24A", "MD774"); M("#4F8FC4", "MD775");
    }

    public static Color Resolve(string? modelNumber, IPodGeneration gen)
    {
        var key = Normalize(modelNumber);
        if (key != null && ByModel.TryGetValue(key, out var c)) return c;
        return Default(gen);
    }

    /// <summary>The generation's default colour when the exact model/colour isn't known.</summary>
    private static Color Default(IPodGeneration g) => g switch
    {
        IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3 => Hex("#D2D2CE"),
        IPodGeneration.Mini1 => Hex("#CDCFCB"),
        IPodGeneration.Mini2 => Hex("#D2D3CF"),
        IPodGeneration.Nano1 => White,
        IPodGeneration.Nano2 => Hex("#D2D3D0"),
        IPodGeneration.Nano3 => Hex("#CFD1CD"),
        IPodGeneration.Nano4 => Hex("#D0D2D4"),
        IPodGeneration.Nano5 => Hex("#28282A"),
        IPodGeneration.Nano6 => Hex("#D0D2D4"),
        IPodGeneration.Nano7 => Hex("#4A4B4E"),
        IPodGeneration.Shuffle => Hex("#D6D6D2"),
        IPodGeneration.Video => Hex("#F4F3EE"),
        _ => White, // 1G–4G, photo, unknown
    };

    /// <summary>Pull the canonical "M9807"-style core out of a raw ModelNumStr (drops region suffixes).</summary>
    private static string? Normalize(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(m.Trim().ToUpperInvariant(), "^[A-Z]+[0-9]+");
        return match.Success ? match.Value : null;
    }
}
