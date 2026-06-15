using System.Text.Json;

namespace iPodCommander;

/// <summary>User customization, persisted to %APPDATA%\Mixtape\settings.json.</summary>
internal sealed class AppSettings
{
    // ---- Appearance ----
    /// <summary>A preset name ("Teal") or a custom "#RRGGBB" hex string.</summary>
    public string Accent { get; set; } = "Teal";
    /// <summary>Background palette: Graphite|Midnight|Carbon|Mocha.</summary>
    public string ThemeVariant { get; set; } = "Graphite";
    public bool Compact { get; set; }          // false = comfortable (52px rows), true = compact (40px)
    public bool ShowArtwork { get; set; } = true;

    // ---- Library ----
    /// <summary>Default column to sort by on load: Playlist|Song|Artist|Album|Time.</summary>
    public string DefaultSort { get; set; } = "Playlist";
    public bool DefaultSortDescending { get; set; }
    public bool ShowVideos { get; set; } = true;   // show the Videos library row (on capable devices)
    public bool ShowPhotos { get; set; } = true;   // show the Photos library row (on capable devices)
    public bool ShowArtist { get; set; } = true;   // Artist column
    public bool ShowAlbum { get; set; } = true;    // Album column
    public bool ShowRating { get; set; } = true;   // Rating (stars) column
    public bool ShowPlays { get; set; } = true;    // Play-count column
    public bool ShowDateAdded { get; set; } = true;// Date-added column
    public bool ShowTime { get; set; } = true;     // Time column

    // ---- Local Music (PC files browsable inside Mixtape) ----
    /// <summary>Folders on the PC scanned for the "Local Music" library view.</summary>
    public List<string> LocalMusicFolders { get; set; } = new();

    // ---- Equalizer (applied to PC playback via NAudio) ----
    public bool EqEnabled { get; set; }
    /// <summary>Per-band gains in dB (10 bands: 31 Hz … 16 kHz). Empty/short = flat.</summary>
    public float[] EqGains { get; set; } = new float[10];

    // ---- Video / transcoding ----
    /// <summary>Transcode target: "Safe" (320x240, plays on 5G + Classic) or "High" (640x480, Classic/late).</summary>
    public string VideoQuality { get; set; } = "Safe";
    /// <summary>Re-encode through ffmpeg even when the source already looks iPod-compatible.</summary>
    public bool AlwaysTranscode { get; set; }
    /// <summary>Manual path to ffmpeg.exe; null/empty = auto-detect on PATH and in the app folder.</summary>
    public string? FfmpegPath { get; set; }

    // ---- Photos ----
    /// <summary>Also store a full-screen image (not just the tiny browse thumbnail) so photos look sharp.</summary>
    public bool PhotoStoreFullResolution { get; set; } = true;

    // ---- Safety ----
    /// <summary>Ask for confirmation before the first write of a session.</summary>
    public bool ConfirmWrites { get; set; } = true;

    // ---- Cover art ----
    /// <summary>Chosen pre-made cover art per target (playlist pid hex, or "lib:&lt;dbid&gt;"); value = CoverArt id.</summary>
    public Dictionary<string, int> Covers { get; set; } = new();

    public int GetCover(string key) => Covers.TryGetValue(key, out int v) ? v : -1;
    public void SetCover(string key, int artId)
    {
        if (artId < 0) Covers.Remove(key); else Covers[key] = artId;
        Save();
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixtape", "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }

    public int RowHeight => Compact ? 40 : 52;

    /// <summary>Resolve <see cref="Accent"/> (preset name or hex) to a colour, falling back to Teal.</summary>
    public Color ResolveAccent()
    {
        if (Accent.StartsWith('#') && TryParseHex(Accent, out var c)) return c;
        foreach (var p in Theme.AccentPresets) if (p.Name == Accent) return p.Color;
        return Theme.AccentPresets[0].Color;
    }

    public static bool TryParseHex(string hex, out Color color)
    {
        color = Color.Empty;
        string s = hex.TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        color = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return true;
    }
}
