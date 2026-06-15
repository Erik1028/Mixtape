namespace iPodCommander;

/// <summary>
/// In-memory representation of an iTunesDB: the database-level identity plus all tracks
/// and playlists. Produced by <see cref="ITunesDbReader"/>; (later) serialized by the writer.
/// </summary>
internal sealed class ITunesDb
{
    /// <summary>mhbd version field (@0x10) — recorded so the writer can echo a compatible value.</summary>
    public uint Version;
    /// <summary>mhbd header length (@0x04) — version-dependent; recorded for the writer.</summary>
    public uint HeaderLength;
    /// <summary>mhbd db persistent id (@0x48).</summary>
    public ulong PersistentId;

    public List<Track> Tracks = new();
    public List<Playlist> Playlists = new();

    /// <summary>Non-fatal anomalies noted while reading (truncated lists, missing master, …).</summary>
    public List<string> Warnings = new();

    /// <summary>
    /// The master/library playlist: the one flagged isMaster, or — for a database that
    /// doesn't byte-flag one — the first non-podcast playlist (libgpod treats the first
    /// playlist as master). The master lists ordinary tracks; podcasts are intentionally
    /// excluded from it, so <see cref="Tracks"/> (the type-1 dataset) is the source of truth
    /// for "every song", not the master playlist.
    /// </summary>
    public Playlist? Master => Playlists.FirstOrDefault(p => p.IsMaster) ?? Playlists.FirstOrDefault(p => !p.IsPodcast);

    private Dictionary<uint, Track>? _byId;

    /// <summary>Fast O(1) lookup from a track's volatile UniqueId (indexed lazily; the DB is immutable after load).</summary>
    public Track? FindByUniqueId(uint uniqueId)
    {
        if (_byId is null)
        {
            _byId = new Dictionary<uint, Track>(Tracks.Count);
            foreach (var t in Tracks) _byId[t.UniqueId] = t; // first wins on the (rare) duplicate id
        }
        return _byId.TryGetValue(uniqueId, out var found) ? found : null;
    }
}
