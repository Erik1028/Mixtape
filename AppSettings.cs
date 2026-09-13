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
    public bool Compact { get; set; }          // false = comfortable (52px rows + art), true = compact (28px, text-only)
    public bool ShowArtwork { get; set; } = true;
    /// <summary>UI language code: "en" | "hu". Empty = auto (follow the OS language). Applied at startup;
    /// changing it restarts the app. See <see cref="Loc"/>.</summary>
    public string Language { get; set; } = "";

    // ---- Playback modes (now-playing bar) ----
    public bool Shuffle { get; set; }
    /// <summary>Repeat mode: Off | All | One.</summary>
    public string RepeatMode { get; set; } = "Off";

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
    public bool FrostedBar { get; set; } = true;
    /// <summary>LAB: the player lives in the window's top deck (transport · now-playing card · utilities), not under the list.</summary>
    public bool BarOnTop { get; set; } = true;   // EXPERIMENT: faint frosted blur of the song list at the top of the player bar
    public bool ShowRemaining { get; set; }      // the deck's total-time slot shows "-remaining" instead (clicking it toggles)
    public bool GlassPopups { get => false; set { } }   // liquid-glass backdrops removed 2026-09-12 (kept so old settings.json files still load)

    // ---- Home page + the side card ----
    /// <summary>The song that was playing when Mixtape last closed ("db:&lt;dbid&gt;" on the iPod, "file:&lt;path&gt;" on the PC)
    /// and where it was — the home page's "Continue listening".</summary>
    public string ResumeTrack { get; set; } = "";
    public double ResumeSeconds { get; set; }
    /// <summary>The Up Next / History / Lyrics side card: open, and which tab.</summary>
    public bool SidePanelOpen { get; set; }
    public string SidePanelTab { get; set; } = "UpNext";

    // ---- Local Music (PC files browsable inside Mixtape) ----
    /// <summary>Folders on the PC scanned for the "Local Music" library view.</summary>
    public List<string> LocalMusicFolders { get; set; } = new();

    /// <summary>User-made playlists of PC files, shown under "ON THIS PC". Each is a name + ordered file paths.</summary>
    public List<LocalPlaylistData> LocalPlaylists { get; set; } = new();

    /// <summary>Smart-playlist rule-sets. The rules live HERE (app-side, per-device) and are evaluated by Mixtape
    /// into a normal iPod playlist's members — so they work on every iPod and can never corrupt the iTunesDB.
    /// Keyed to the iPod playlist by <see cref="SmartPlaylistDef.PersistentId"/>.</summary>
    public List<SmartPlaylistDef> SmartPlaylists { get; set; } = new();

    /// <summary>Photo-grid tile size in px (the size slider on the Photos view).</summary>
    public int PhotoTileSize { get; set; } = 132;

    // ---- Equalizer (applied to PC playback via NAudio) ----
    public bool EqEnabled { get; set; }
    /// <summary>Per-band gains in dB (10 bands: 31 Hz … 16 kHz). Empty/short = flat.</summary>
    public float[] EqGains { get; set; } = new float[10];

    // ---- Pro playback features (advanced; off by default so default playback is the proven path) ----
    /// <summary>Gapless playback: no silence between back-to-back tracks.</summary>
    public bool GaplessEnabled { get; set; }
    /// <summary>Crossfade: blend the end of one track into the start of the next.</summary>
    public bool CrossfadeEnabled { get; set; }
    /// <summary>Crossfade length in seconds (UI clamps 1..12).</summary>
    public double CrossfadeSeconds { get; set; } = 6.0;
    /// <summary>Even out loudness across tracks (RMS-based normalization gain).</summary>
    public bool NormalizeVolume { get; set; }
    /// <summary>The player's volume slider, 0..1, as it was when the app last closed (0 = it was muted).</summary>
    public double Volume { get; set; } = 1.0;
    /// <summary>Downmix playback to mono (single channel through both speakers).</summary>
    public bool MonoOutput { get; set; }

    // ---- Lyrics ----
    /// <summary>Fetch time-synced lyrics from LRCLIB when the song has none locally. Only ever asked while
    /// the lyrics panel is open, and only the artist + title + length are sent. A .lrc file next to the
    /// audio, or lyrics embedded in its tags, are always preferred and need no network at all.</summary>
    public bool OnlineLyrics { get; set; } = true;
    /// <summary>Fetch a cover from the internet for albums whose files carry none (artist + album are sent).</summary>
    public bool OnlineCovers { get; set; } = true;
    /// <summary>Also write a downloaded cover into the PC file's own tag (never an iPod file).</summary>
    public bool EmbedDownloadedCovers { get; set; }

    // ---- Discord Rich Presence ----
    /// <summary>Show the song Mixtape is playing on your Discord profile (off by default — nothing leaves
    /// the machine until you switch it on; it talks only to the local Discord client).</summary>
    public bool DiscordRichPresence { get; set; }
    /// <summary>Discord Application ID from the Developer Portal. Public by design (it only names the app
    /// shown in Discord and carries no secret); empty = the feature stays off.</summary>
    public string DiscordAppId { get; set; } = "";
    /// <summary>Show the real album cover on the Discord card. Discord can only display images it can fetch
    /// itself, so this looks the cover up by artist+album on Apple's public search API — the one part of the
    /// feature that uses the network. Off by default; results are cached so each album is asked once.</summary>
    public bool DiscordCoverArt { get; set; }

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
    /// <summary>When a hash58 iPod with no stored GUID is detected, offer to read its hardware ID
    /// automatically (instead of making the user find the "Read device ID" button on the device page).</summary>
    public bool AutoGuidRecovery { get; set; } = true;

    // ---- Cover art ----
    /// <summary>Chosen pre-made cover art per target (playlist pid hex, or "lib:&lt;dbid&gt;"); value = CoverArt id.</summary>
    public Dictionary<string, int> Covers { get; set; } = new();

    public int GetCover(string key) => Covers.TryGetValue(key, out int v) ? v : -1;
    public void SetCover(string key, int artId)
    {
        if (artId < 0) Covers.Remove(key); else Covers[key] = artId;
        Save();
    }

    // ---- Lyrics ----
    /// <summary>The listener's own sync nudge per song, in milliseconds (+ = the words are sung LATER than
    /// the sheet says). Community sheets are timed against one particular copy of a recording, so a shift of
    /// a second or two is normal and no lookup can correct it. Keyed by <c>LyricsLookup.SongKey</c>, which
    /// ignores the track length on purpose, so the adjustment survives a re-fetch or a re-encode.</summary>
    public Dictionary<string, int> LyricSync { get; set; } = new();

    // A hand-edited (or older) settings.json can carry an explicit null, which deserializes over the
    // initializer — the same guard the rest of this file uses for its other collections.
    public int GetLyricSync(string key) => (LyricSync ??= new()).TryGetValue(key, out int v) ? v : 0;
    public void SetLyricSync(string key, int ms)
    {
        LyricSync ??= new();
        if (ms == 0) LyricSync.Remove(key); else LyricSync[key] = ms;
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

    /// <summary>Set by the render harness: the previews must never write the user's real settings.json (opening
    /// the side card, a bookmark tick, a volume push all call Save).</summary>
    public static bool Frozen;

    public void Save()
    {
        if (Frozen) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var mine = JsonSerializer.SerializeToNode(this, opts)!.AsObject();

            // Merge over the existing file so we don't wipe keys written by the Avalonia app
            // (it persists to the same settings.json via its own AppConfig). Our fields win;
            // any keys we don't know about are preserved.
            System.Text.Json.Nodes.JsonObject merged = mine;
            try
            {
                if (File.Exists(FilePath) &&
                    System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath)) is System.Text.Json.Nodes.JsonObject existing)
                {
                    foreach (var kv in mine) existing[kv.Key] = kv.Value?.DeepClone();
                    merged = existing;
                }
            }
            catch { /* unreadable existing file → just write ours */ }

            // Atomic: write a temp file then swap it in, so a crash mid-write can't truncate settings.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, merged.ToJsonString(opts));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch { /* settings are best-effort */ }
    }

    public int RowHeight => Compact ? 28 : 56;   // comfortable = a 40 px row with real air (was 52)

    /// <summary>Whether the song list shows the artwork column. Compact mode is text-only (iTunes-style),
    /// so the cover column is dropped there regardless of <see cref="ShowArtwork"/> — that art is also what
    /// forces the taller rows, so hiding it is what makes compact truly compact.</summary>
    public bool ListArtwork => ShowArtwork && !Compact;

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

/// <summary>A PC-side playlist: a name plus an ordered list of local audio file paths.</summary>
internal sealed class LocalPlaylistData
{
    /// <summary>Stable id used to key a chosen cover (and any future per-playlist preference) so it survives a
    /// rename. Assigned lazily the first time a cover is set; empty for playlists that never got one.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

// SmartRule + SmartPlaylistDef moved to Mixtape.Core/SmartPlaylist.cs (shared with the cross-platform app);
// the AppSettings.SmartPlaylists list below still holds them — same `iPodCommander` namespace, so nothing changed here.
