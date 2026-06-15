namespace iPodCommander;

/// <summary>
/// Copies tracks OFF the iPod back to the PC — the classic "rescue my old iPod's music" job. The
/// audio files live on the device under <c>iPod_Control/Music/Fnn</c> with cryptic names (ipcm…/
/// libgpod…); this writes them out with readable <c>Artist/Album/NN Title.ext</c> paths and, best-effort,
/// refreshes the file's tags from the iTunesDB metadata (which is authoritative) so the rescued files
/// are clean even if the on-device tags were sparse. Pure read of the device — never writes to it.
/// </summary>
internal static class MusicExporter
{
    /// <summary>
    /// Copy one track's file to <paramref name="destRoot"/>. Returns the written path, or null if the
    /// track has no file on the device. <paramref name="organize"/> nests into Artist/Album folders;
    /// <paramref name="applyTags"/> rewrites the copy's tags from the database metadata.
    /// </summary>
    public static string? ExportOne(Track t, string mountRoot, string destRoot, bool organize, bool applyTags)
    {
        string? src = t.ResolveFilePath(mountRoot);
        if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;

        string ext = Path.GetExtension(src).ToLowerInvariant();
        string dir = organize
            ? Path.Combine(destRoot, Safe(FirstNonEmpty(t.AlbumArtist, t.Artist, "Unknown Artist")), Safe(FirstNonEmpty(t.Album, "Unknown Album")))
            : destRoot;
        Directory.CreateDirectory(dir);

        string dest = UniquePath(dir, BuildName(t), ext);
        File.Copy(src, dest, overwrite: false);
        // A reserved DOS device name (CON/NUL/…) used as a filename makes File.Copy "succeed" while writing
        // nothing — verify the file really landed so such tracks are reported as failed, not as false successes.
        if (!File.Exists(dest)) throw new IOException("Windows rejected the destination filename.");
        if (applyTags) TryApplyTags(dest, t);
        return dest;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string BuildName(Track t)
    {
        string title = Safe(FirstNonEmpty(t.Title, "Untitled"));
        if (t.TrackNumber > 0) return $"{t.TrackNumber:00} {title}";
        string artist = Safe(t.Artist ?? "");
        return artist.Length > 0 && artist != "_" ? $"{artist} - {title}" : title;
    }

    private static string UniquePath(string dir, string baseName, string ext)
    {
        // Keep the whole path under Windows' MAX_PATH even when Artist/Album/Title are all long, so the
        // copy succeeds instead of throwing on a deep destination. Leave room for a " (NN)" dedup suffix.
        int budget = 255 - dir.Length - 1 - ext.Length - 6;
        if (budget >= 4 && baseName.Length > budget) baseName = baseName[..budget].TrimEnd('.', ' ');

        string p = Path.Combine(dir, baseName + ext);
        for (int n = 2; File.Exists(p); n++) p = Path.Combine(dir, $"{baseName} ({n}){ext}");
        return p;
    }

    private static void TryApplyTags(string path, Track t)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(t.Title)) f.Tag.Title = t.Title;
            if (!string.IsNullOrWhiteSpace(t.Artist)) f.Tag.Performers = new[] { t.Artist! };
            if (!string.IsNullOrWhiteSpace(t.AlbumArtist)) f.Tag.AlbumArtists = new[] { t.AlbumArtist! };
            if (!string.IsNullOrWhiteSpace(t.Album)) f.Tag.Album = t.Album;
            if (!string.IsNullOrWhiteSpace(t.Genre)) f.Tag.Genres = new[] { t.Genre! };
            if (t.Year > 0) f.Tag.Year = t.Year;
            if (t.TrackNumber > 0) f.Tag.Track = t.TrackNumber;
            if (t.TotalTracks > 0) f.Tag.TrackCount = t.TotalTracks;
            f.Save();
        }
        catch { /* best-effort: the copy is kept even if retagging fails (e.g. an exotic format) */ }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v!;
        return "";
    }

    private static string Safe(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.', ' '); // Windows rejects trailing dot/space on a path component
        if (s.Length == 0) s = "_";
        if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
        // A legacy DOS device name (CON, NUL, COM1…) is illegal as ANY path component (folder or file),
        // matched on the part before the first dot, case-insensitively — prefix '_' to neutralize it.
        int dot = s.IndexOf('.');
        if (Reserved.Contains(dot < 0 ? s : s[..dot])) s = "_" + s;
        return s;
    }
}
