namespace iPodCommander;

/// <summary>
/// A playlist (mhyp). Members are stored as an ordered list of track <see cref="Track.UniqueId"/>
/// values (mhip.trackId references). One playlist is the master/library list
/// (<see cref="IsMaster"/>); it lists ordinary tracks but podcast tracks are intentionally
/// excluded from it, so it is not guaranteed to contain every track.
/// </summary>
internal sealed class Playlist
{
    public string Name = "";
    public ulong PersistentId;
    public bool IsMaster;
    public bool IsPodcast;
    public uint SortOrder;

    /// <summary>Ordered member track ids (each equals some <see cref="Track.UniqueId"/>).</summary>
    public List<uint> TrackIds = new();

    public int Count => TrackIds.Count;
    public string DisplayName => IsMaster ? $"{(string.IsNullOrEmpty(Name) ? "Library" : Name)} (all songs)" : Name;
}
