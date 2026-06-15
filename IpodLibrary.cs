namespace iPodCommander;

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

    private IpodLibrary(IPodDevice device, RawDb raw, ITunesDb view)
    {
        Device = device;
        Raw = raw;
        View = view;
    }

    public static IpodLibrary Load(IPodDevice device)
    {
        byte[] bytes = File.ReadAllBytes(device.ITunesDbPath);
        return new IpodLibrary(device, RawDb.Parse(bytes), ITunesDbReader.Read(bytes));
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

    /// <summary>Delete a playlist but keep all its songs in the library. Does not save.</summary>
    public void RemovePlaylist(Playlist pl) => Raw.RemovePlaylist(pl.PersistentId);

    /// <summary>Rename a playlist. Does not save.</summary>
    public void RenamePlaylist(Playlist pl, string newName) => Raw.RenamePlaylist(pl.PersistentId, newName);

    /// <summary>Remove tracks from a playlist while keeping them in the library. Does not save.</summary>
    public void RemoveFromPlaylist(Playlist pl, IEnumerable<uint> trackIds) =>
        Raw.RemoveTracksFromPlaylist(pl.PersistentId, new HashSet<uint>(trackIds));

    /// <summary>Edit a track's tags/rating in the database. Returns false if the track wasn't found. Does not save.</summary>
    public bool EditTrack(uint uniqueId, TrackEdit edit) => Raw.EditTrack(uniqueId, edit);

    /// <summary>Reorder a playlist's tracks to the given unique-id order. Does not save.</summary>
    public void ReorderPlaylist(Playlist pl, IList<uint> order) => Raw.ReorderPlaylist(pl.PersistentId, order);

    /// <summary>Create a new empty playlist; returns its persistent id. Does not save.</summary>
    public ulong CreatePlaylist(string name) => Raw.CreatePlaylist(name);

    /// <summary>Add existing library tracks to a playlist. Does not save.</summary>
    public void AddToPlaylist(ulong playlistPid, IEnumerable<uint> trackIds) => Raw.AddTracksToPlaylist(playlistPid, trackIds);

    public void Save()
    {
        byte[] bytes = Raw.Serialize();
        ChecksumWriter.Apply(bytes, Device.Profile.Scheme, Device.Profile.FirewireGuid); // sign for hash58 devices; no-op for NONE
        SafeDbWriter.Write(Device, bytes, Raw.TrackCount);
        byte[] fresh = File.ReadAllBytes(Device.ITunesDbPath);
        View = ITunesDbReader.Read(fresh);
        Raw = RawDb.Parse(fresh);
    }

    private static ulong RandomDbid()
    {
        Span<byte> b = stackalloc byte[8];
        Random.Shared.NextBytes(b);
        return BitConverter.ToUInt64(b);
    }
}
