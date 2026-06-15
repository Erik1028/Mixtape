namespace iPodCommander;

/// <summary>
/// One song on the iPod. Carries the mhit fixed fields we care about plus the child
/// mhod strings (title/artist/album/…) and the resolved on-disk location.
///
/// Two identifiers matter and must not be confused:
///   • <see cref="UniqueId"/> (mhit @0x10) is a 32-bit, VOLATILE id that playlists
///     reference (mhip.trackId). It may be reassigned on every full DB rewrite.
///   • <see cref="Dbid"/> (mhit @0x70) is the 64-bit PERSISTENT identity; keep it stable
///     for the life of the track.
/// </summary>
internal sealed class Track
{
    // --- identity ---
    public uint UniqueId;
    public ulong Dbid;

    // --- strings (child mhods) ---
    public string? Title;
    public string? Artist;
    public string? Album;
    public string? AlbumArtist;
    public string? Genre;
    public string? Composer;
    public string? Comment;
    public string? FileTypeDescription;   // mhod type 6, e.g. "MPEG audio file"
    /// <summary>mhod type 2 — the ':'-separated iPod-relative path, e.g. ":iPod_Control:Music:F12:libgpod000123.mp3".</summary>
    public string? Location;

    /// <summary>For a track in the PC "Local Music" library (not on an iPod): the absolute file path. Null otherwise.</summary>
    public string? LocalPath;

    // --- numeric mhit fields ---
    public uint FileSize;       // bytes (@0x24)
    public uint LengthMs;       // duration (@0x28)
    public uint TrackNumber;    // (@0x2C)
    public uint TotalTracks;    // (@0x30)
    public uint Year;           // (@0x34)
    public uint Bitrate;        // kbps (@0x38)
    public uint SampleRate;     // Hz — the field stores Hz<<16 (@0x3C)
    public uint DiscNumber;     // (@0x5C)
    public uint TotalDiscs;     // (@0x60)
    public uint PlayCount;      // (@0x50)
    public byte Rating;         // stars*20 (@0x1F)
    public uint MediaType;      // 1=audio … (@0xD0)
    public DateTime? DateAdded; // (@0x68)
    public DateTime? LastPlayed;// (@0x58)

    /// <summary>The OS path on this PC, derived from <see cref="Location"/> + the mount root. For convenience only.</summary>
    public string? ResolveFilePath(string mountRoot)
    {
        if (string.IsNullOrEmpty(Location)) return null;
        // ":iPod_Control:Music:F12:foo.mp3" → "iPod_Control\Music\F12\foo.mp3"
        string rel = Location.TrimStart(':').Replace(':', Path.DirectorySeparatorChar);
        return Path.Combine(mountRoot, rel);
    }

    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "(untitled)" : Title!;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(LengthMs);

    /// <summary>Human "m:ss" for songs, but "h:mm:ss" once the length reaches an hour — otherwise the
    /// TimeSpan "m" specifier silently drops the hours and long videos read far shorter than they are.</summary>
    public string DurationStr => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}
