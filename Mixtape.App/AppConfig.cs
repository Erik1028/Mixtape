using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mixtape.App;

/// <summary>Reads/writes the accent + theme in the SAME %APPDATA%\Mixtape\settings.json the Windows app
/// uses, merging so the other fields are preserved (and the two apps stay in sync).</summary>
internal static class AppConfig
{
    private static string FilePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Mixtape", "settings.json");

    public static (string accent, string variant) Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o)
                return (o["Accent"]?.GetValue<string>() ?? "Teal", o["ThemeVariant"]?.GetValue<string>() ?? "Graphite");
        }
        catch { }
        return ("Teal", "Graphite");
    }

    public static void Save(string accent, string variant)
    {
        try
        {
            JsonObject o = (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject e) ? e : new JsonObject();
            o["Accent"] = accent;
            o["ThemeVariant"] = variant;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
