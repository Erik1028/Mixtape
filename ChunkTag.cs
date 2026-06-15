namespace iPodCommander;

/// <summary>
/// The 4-character chunk tags of the iTunesDB tree, and the single source of truth for the
/// one rule that bites everyone: in LIST chunks (mhlt/mhlp/mhla) the u32 at offset 8 is a
/// CHILD COUNT; in every other (container) chunk it is a TOTAL BYTE LENGTH. Mixing these up
/// is the #1 cause of a corrupted database, so both the reader and the writer go through
/// <see cref="IsListChunk"/>.
/// </summary>
internal static class ChunkTag
{
    public const string Mhbd = "mhbd"; // database (root)
    public const string Mhsd = "mhsd"; // dataset (type: 1=tracks 2=playlists 3=podcasts 4=albums 5=smartPL)
    public const string Mhlt = "mhlt"; // track list  (LIST: off8 = #mhit)
    public const string Mhit = "mhit"; // track item
    public const string Mhod = "mhod"; // data object (strings etc.)
    public const string Mhlp = "mhlp"; // playlist list (LIST: off8 = #mhyp)
    public const string Mhyp = "mhyp"; // playlist header
    public const string Mhip = "mhip"; // playlist item (references a track)
    public const string Mhla = "mhla"; // album list (LIST: off8 = #mhia)
    public const string Mhia = "mhia"; // album item

    public static bool IsListChunk(string tag) => tag is Mhlt or Mhlp or Mhla;
}
