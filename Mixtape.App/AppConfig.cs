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

    /// <summary>Shuffle + repeat mode, shared with the Windows app (keys "Shuffle" bool + "RepeatMode" enum name).</summary>
    public static (bool shuffle, string repeat) LoadModes()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o)
                return (o["Shuffle"]?.GetValue<bool>() ?? false, o["RepeatMode"]?.GetValue<string>() ?? "Off");
        }
        catch { }
        return (false, "Off");
    }

    public static void SaveModes(bool shuffle, string repeat) => Merge(o =>
    {
        o["Shuffle"] = shuffle;
        o["RepeatMode"] = repeat;
    });

    /// <summary>UI language, shared with the Windows app ("" = auto-detect, "en", "hu").</summary>
    public static string LoadLanguage()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o)
                return o["Language"]?.GetValue<string>() ?? "";
        }
        catch { }
        return "";
    }

    public static void SaveLanguage(string code) => Merge(o => o["Language"] = code);

    public static void Save(string accent, string variant) => Merge(o =>
    {
        o["Accent"] = accent;
        o["ThemeVariant"] = variant;
    });

    /// <summary>Read-modify-write the shared settings.json atomically, preserving every key the other app wrote.</summary>
    private static void Merge(Action<JsonObject> mutate)
    {
        try
        {
            JsonObject o = (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject e) ? e : new JsonObject();
            mutate(o);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Atomic write (temp + replace), mirroring the WinForms AppSettings.Save — so a crash mid-write can't
            // truncate the settings.json the two apps share and wipe the Windows app's library/playlist/cover data.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch { }
    }
}
