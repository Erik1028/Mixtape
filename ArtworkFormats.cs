namespace iPodCommander;

/// <summary>
/// The RGB565 little-endian COVER-ART thumbnail formats Mixtape generates per device generation,
/// taken from libgpod's <c>itdb_device.c</c> artwork tables. These are distinct from the photo
/// formats in <see cref="PhotoFormats"/> (different correlation ids). We render a small "list"
/// thumbnail plus a larger "now playing" image per model — enough for the iPod to show art in lists
/// and on the now-playing screen. Reuses <see cref="PhotoFormat"/> (id + slot dimensions).
/// </summary>
internal static class ArtworkFormats
{
    private static readonly PhotoFormat[] Photo = { new(1017, 56, 56, false), new(1016, 140, 140, true) };     // iPod photo (4G)
    private static readonly PhotoFormat[] Video = { new(1028, 100, 100, false), new(1029, 200, 200, true) };   // 5G video
    private static readonly PhotoFormat[] Nano12 = { new(1031, 42, 42, false), new(1027, 100, 100, true) };    // nano 1G/2G
    private static readonly PhotoFormat[] Nano3 = { new(1061, 56, 56, false), new(1055, 128, 128, true) };     // nano 3G
    private static readonly PhotoFormat[] Nano4 = { new(1055, 128, 128, false), new(1071, 240, 240, true) };   // nano 4G
    private static readonly PhotoFormat[] Nano5 = { new(1078, 80, 80, false), new(1073, 240, 240, true) };     // nano 5G
    private static readonly PhotoFormat[] Classic = { new(1061, 56, 56, false), new(1055, 128, 128, true) };   // classic 1/2/3 (+ shared list thumb)

    /// <summary>Album-art formats for this device, or empty if the model has no on-screen artwork.</summary>
    public static PhotoFormat[] For(IPodGeneration gen) => gen switch
    {
        IPodGeneration.Photo => Photo,
        IPodGeneration.Video => Video,
        IPodGeneration.Nano1 or IPodGeneration.Nano2 => Nano12,
        IPodGeneration.Nano3 => Nano3,
        IPodGeneration.Nano4 => Nano4,
        IPodGeneration.Nano5 => Nano5,
        IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3 => Classic,
        _ => System.Array.Empty<PhotoFormat>(),
    };

    private static readonly Dictionary<int, PhotoFormat> Known =
        new[] { Photo, Video, Nano12, Nano3, Nano4, Nano5, Classic }.SelectMany(a => a)
            .GroupBy(f => f.FormatId).ToDictionary(g => g.Key, g => g.First());

    /// <summary>Slot dimensions for a known artwork format id (so existing device formats can be reused).</summary>
    public static PhotoFormat? Lookup(int formatId) => Known.TryGetValue(formatId, out var f) ? f : null;
}
