namespace iPodCommander;

/// <summary>
/// Stages and writes album art to the iPod's ArtworkDB + .ithmb files. Implemented in the Windows
/// app (it needs System.Drawing); Core only depends on this interface so artwork can be wired into
/// the normal add/save flow without Core taking a graphics dependency.
/// </summary>
internal interface IArtworkSink
{
    /// <summary>True only on colour-screen devices whose ArtworkDB we can safely write.</summary>
    bool SupportsArtwork { get; }
    /// <summary>Render + stage one track's cover from <paramref name="sourcePath"/>; returns the
    /// assigned ArtworkDB image id and the total thumbnail byte size, or null if the file has no art.</summary>
    (uint MhiiId, uint Size)? Stage(ulong trackDbid, string sourcePath);
    /// <summary>Write the staged artwork (append .ithmb + rebuild ArtworkDB). Best-effort.</summary>
    void Commit();
}

/// <summary>
/// Orchestrates reading + editing + saving one iPod's library. Holds both a display model
/// (<see cref="View"/>, parsed for the UI) and the byte-preserving edit model
/// (<see cref="RawDb"/>). Edits stage into the RawDb; <see cref="Save"/> serializes it,
/// writes it via <see cref="SafeDbWriter"/>, then reloads both models from the new bytes.
/// </summary>
internal sealed class IpodLibrary
{
    public IPodDevice Device { get; }
    public RawDb Raw { get; private set; }
    public ITunesDb View { get; private set; }

    /// <summary>Optional cover-art writer (set by the host on colour-screen devices). Null = no artwork sync.</summary>
    public IArtworkSink? Artwork { get; set; }

    private IpodLibrary(IPodDevice device, RawDb raw, ITunesDb view)
    {
        Device = device;
        Raw = raw;
        View = view;
    }

    public static IpodLibrary Load(IPodDevice device)
    {
        byte[] bytes = File.ReadAllBytes(device.ITunesDbPath);
        var lib = new IpodLibrary(device, RawDb.Parse(bytes), ITunesDbReader.Read(bytes));
        PlayCounts.Apply(lib); // overlay on-device play counts/ratings (the data iTunes would merge on sync)
        return lib;
    }

    /// <summary>
    /// Copy one audio file onto the iPod and stage it in the database. The file is copied
    /// FIRST (an orphan file is harmless; a DB entry pointing at a missing file is not), then
    /// the mhit/mhip are added. Does not save — batch several adds, then <see cref="Save"/> once.
    /// Returns the track's display title.
    /// </summary>
    public string AddFile(string sourcePath) => AddMediaFile(sourcePath, MediaType.Audio);

    /// <summary>
    /// Generalised add. <paramref name="fileToCopy"/> is the bytes that land on the iPod (e.g. a
    /// transcoded .m4v); metadata/duration are read from it. <paramref name="mediaType"/> picks the
    /// mhit media kind (audio vs movie/music-video/TV-show). <paramref name="titleOverride"/> sets a
    /// nicer title when the copied file is a tagless transcode. Does not save.
    /// </summary>
    public string AddMediaFile(string fileToCopy, uint mediaType, string? titleOverride = null, double durationSecHint = 0)
    {
        var nt = MetadataExtractor.Read(fileToCopy, isVideo: MediaType.IsVideo(mediaType));
        if (!string.IsNullOrWhiteSpace(titleOverride)) nt.Title = titleOverride;
        // A video must carry its duration (mhit tracklen). If TagLib couldn't read it from the
        // transcoded container, fall back to the duration ffmpeg already measured.
        if (nt.LengthMs == 0 && durationSecHint > 0) nt.LengthMs = (uint)Math.Round(durationSecHint * 1000);
        nt.MediaType = mediaType;
        var (location, _) = MusicCopier.Copy(Device, fileToCopy);
        nt.Location = location;
        nt.UniqueId = Raw.MaxUniqueId() + 1;
        nt.Dbid = RandomDbid();
        // Stage cover art (colour-screen devices only); link the track's mhit to its ArtworkDB image.
        if (Artwork is { SupportsArtwork: true } sink && !MediaType.IsVideo(mediaType) && sink.Stage(nt.Dbid, fileToCopy) is { } art)
        {
            nt.HasArtwork = true;
            nt.MhiiLink = art.MhiiId;
            nt.ArtworkSize = art.Size;
        }
        Raw.AddTrack(RawDb.BuildMhitChunk(nt), nt.UniqueId);
        return string.IsNullOrEmpty(nt.Title) ? Path.GetFileName(fileToCopy) : nt.Title!;
    }

    /// <summary>Remove a track from the database (and optionally delete its audio file). Does not save.</summary>
    public void DeleteTrack(uint uniqueId, bool deleteFile)
    {
        Track? t = View.FindByUniqueId(uniqueId);
        Raw.RemoveTrack(uniqueId);
        if (deleteFile && t?.Location is { Length: > 0 } loc)
        {
            try
            {
                string p = t.ResolveFilePath(Device.MountRoot) ?? "";
                if (p.Length > 0 && File.Exists(p)) File.Delete(p);
            }
            catch { /* a leftover audio file is harmless; the DB no longer references it */ }
        }
    }

    /// <summary>Delete a playlist but keep all its songs in the library. Does not save. Returns false if the
    /// playlist couldn't be matched (e.g. an externally-authored list with no persistent id) — the DB is unchanged.</summary>
    public bool RemovePlaylist(Playlist pl) => Raw.RemovePlaylist(pl.PersistentId);

    /// <summary>Rename a playlist. Does not save. Returns false if it couldn't be matched (unchanged DB).</summary>
    public bool RenamePlaylist(Playlist pl, string newName) => Raw.RenamePlaylist(pl.PersistentId, newName);

    /// <summary>Remove tracks from a playlist while keeping them in the library. Does not save. Returns false if unchanged.</summary>
    public bool RemoveFromPlaylist(Playlist pl, IEnumerable<uint> trackIds) =>
        Raw.RemoveTracksFromPlaylist(pl.PersistentId, new HashSet<uint>(trackIds));

    /// <summary>Edit a track's tags/rating in the database. Returns false if the track wasn't found. Does not save.</summary>
    public bool EditTrack(uint uniqueId, TrackEdit edit) => Raw.EditTrack(uniqueId, edit);

    /// <summary>Reorder a playlist's tracks to the given unique-id order. Does not save. Returns false if unchanged.</summary>
    public bool ReorderPlaylist(Playlist pl, IList<uint> order) => Raw.ReorderPlaylist(pl.PersistentId, order);

    /// <summary>Create a new empty playlist; returns its persistent id. Does not save.</summary>
    public ulong CreatePlaylist(string name) => Raw.CreatePlaylist(name);

    /// <summary>Add existing library tracks to a playlist. Does not save. Returns false if the playlist couldn't be matched.</summary>
    public bool AddToPlaylist(ulong playlistPid, IEnumerable<uint> trackIds) => Raw.AddTracksToPlaylist(playlistPid, trackIds);

    public void Save()
    {
        // Fold the on-device "Play Counts" into the DB so the save PERSISTS them (the iTunes-style sync). It
        // returns a pre-fold snapshot so a failed write can undo ONLY the fold (keeping the user's staged edits),
        // and the file is deleted only AFTER a successful write so plays are never added twice.
        var fold = PlayCounts.FoldIntoRaw(this);

        byte[] bytes = Raw.Serialize();
        try
        {
            ChecksumWriter.Apply(bytes, Device.Profile.Scheme, Device.Profile.FirewireGuid); // sign for hash58 devices; no-op for NONE
            SafeDbWriter.Write(Device, bytes, Raw.TrackCount);
        }
        catch
        {
            if (fold is { } undo) Raw = RawDb.Parse(undo.preFold); // roll the fold back so a retry won't double-count
            throw;
        }
        // The iTunesDB is now safely on disk. Write the ArtworkDB after it (best-effort: a failure here
        // leaves tracks flagged for art with no thumbnails — the iPod simply shows none, never corruption).
        try { Artwork?.Commit(); } catch { }
        if (fold is { } done) { try { File.Delete(done.path); } catch { } } // folded + written → clear so plays aren't re-added
        byte[] fresh = File.ReadAllBytes(Device.ITunesDbPath);
        View = ITunesDbReader.Read(fresh);
        Raw = RawDb.Parse(fresh);
        PlayCounts.Apply(this); // keep any (now cleared) on-device play counts/ratings visible after a save+reload
    }

    private static ulong RandomDbid()
    {
        Span<byte> b = stackalloc byte[8];
        Random.Shared.NextBytes(b);
        return BitConverter.ToUInt64(b);
    }
}
