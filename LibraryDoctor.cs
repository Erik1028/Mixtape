namespace iPodCommander;

/// <summary>
/// Read-only health scan of an iPod library: cross-checks the iTunesDB against the files actually on the
/// drive and against itself. Produces a <see cref="DoctorReport"/> the UI shows; the user then chooses
/// which safe fixes to apply (a <see cref="DoctorPlan"/>). The scan NEVER writes — all changes go through
/// the host's normal IpodLibrary.DeleteTrack/Save path, which already removes a track from every playlist
/// (RawDb.RemoveTrack) so deletes can't leave dangling references.
/// </summary>
internal sealed class DoctorReport
{
    public int TotalTracks;
    public List<Track> MissingFiles = new();                 // DB rows whose audio file is gone
    public List<(string Path, long Size)> OrphanFiles = new();// media files on the drive no track points at
    public List<List<Track>> DuplicateGroups = new();        // each group sorted best-keeper-first
    public int IncompleteTags;                               // songs missing title/artist/album
    public int AlbumGaps;                                    // albums with a track-number gap (report only)
    public long OrphanBytes;
    public int DuplicateExtras;                             // total copies beyond one-per-group

    public bool Clean => MissingFiles.Count == 0 && OrphanFiles.Count == 0
                         && DuplicateExtras == 0 && IncompleteTags == 0 && AlbumGaps == 0;
}

/// <summary>The user's chosen fixes. Ids reference Track.UniqueId; files are absolute OS paths.</summary>
internal sealed class DoctorPlan
{
    public List<uint> RemoveRows = new();      // missing-file rows → DeleteTrack(id, deleteFile:false)
    public List<uint> DeleteDupTracks = new(); // duplicate extras → row removed; file deleted (guarded)
    public List<string> DeleteFiles = new();   // orphan files → File.Delete (guarded against survivors)

    public bool HasActions => RemoveRows.Count > 0 || DeleteDupTracks.Count > 0 || DeleteFiles.Count > 0;
}

internal static class LibraryDoctor
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".aif", ".aiff", ".m4v", ".mp4", ".mov", ".alac", ".flac", ".aax" };

    public static DoctorReport Scan(IpodLibrary lib)
    {
        var dev = lib.Device;
        var tracks = lib.View.Tracks;
        var rep = new DoctorReport { TotalTracks = tracks.Count };

        // 1) Referenced files + missing files. Build the set of paths the DB points at; flag any that are gone.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            string? p = t.ResolveFilePath(dev.MountRoot);
            if (string.IsNullOrEmpty(p)) continue;
            try { referenced.Add(Path.GetFullPath(p)); } catch { /* unparseable path → can't dedupe it, skip */ }
            if (!File.Exists(p)) rep.MissingFiles.Add(t);
        }

        // 2) Orphan media files: anything under the Music dir that no track references (wasted space).
        try
        {
            if (Directory.Exists(dev.MusicDir))
                foreach (var f in Directory.EnumerateFiles(dev.MusicDir, "*", SearchOption.AllDirectories))
                {
                    if (!MediaExts.Contains(Path.GetExtension(f))) continue;   // never touch non-media files
                    string full; try { full = Path.GetFullPath(f); } catch { continue; }
                    if (referenced.Contains(full)) continue;
                    long size = 0; try { size = new FileInfo(f).Length; } catch { }
                    rep.OrphanFiles.Add((f, size));
                    rep.OrphanBytes += size;
                }
        }
        catch { /* enumeration failure (drive yanked) → just report no orphans */ }

        // 3) Duplicates: same normalized title+artist+album AND near-equal length (2 s bucket, so different
        //    versions/remasters aren't merged). Keeper = most-played, then highest bitrate.
        foreach (var g in tracks.Where(t => !MediaType.IsVideo(t.MediaType))
                                 .GroupBy(DupKey)
                                 .Where(g => g.Count() > 1))
        {
            var list = g.OrderByDescending(t => t.PlayCount)
                        .ThenByDescending(t => t.Bitrate)
                        .ThenBy(t => t.UniqueId)
                        .ToList();
            rep.DuplicateGroups.Add(list);
            rep.DuplicateExtras += list.Count - 1;
        }

        // 4) Incomplete tags (songs only).
        foreach (var t in tracks)
            if (!MediaType.IsVideo(t.MediaType) && (Blank(t.Title) || Blank(t.Artist) || Blank(t.Album)))
                rep.IncompleteTags++;

        // 5) Track-number gaps (report only; conservative to avoid false alarms on partial albums).
        rep.AlbumGaps = CountAlbumGaps(tracks);

        return rep;
    }

    private static string DupKey(Track t)
    {
        long bucket = t.LengthMs / 2000;   // 2-second buckets
        return Norm(t.Title) + "" + Norm(t.Artist) + "" + Norm(t.Album) + "" + bucket;
    }

    private static string Norm(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Blank(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length == 0 || s.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountAlbumGaps(IReadOnlyList<Track> tracks)
    {
        int gaps = 0;
        foreach (var g in tracks.Where(t => !Blank(t.Album) && t.TrackNumber > 0).GroupBy(t => Norm(t.Album)))
        {
            var nums = g.Select(t => (int)t.TrackNumber).Where(n => n is > 0 and <= 99).Distinct().OrderBy(n => n).ToList();
            if (nums.Count < 3) continue;          // need a few tracks before "gap" means anything
            int max = nums[^1];
            if (max > nums.Count && max <= 60) gaps++;   // some number in 1..max is missing
        }
        return gaps;
    }
}
