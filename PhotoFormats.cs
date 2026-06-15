namespace iPodCommander;

/// <summary>One device image format: its correlation id + the fixed slot dimensions.</summary>
internal sealed record PhotoFormat(int FormatId, int Width, int Height, bool FullScreen)
{
    /// <summary>Bytes one slot occupies in the .ithmb (RGB565 = 2 bytes/pixel).</summary>
    public int SlotBytes => Width * Height * 2;
}

/// <summary>
/// The RGB565 little-endian photo formats Mixtape generates per device generation, taken verbatim
/// from libgpod's <c>itdb_device.c</c> photo-format tables. We deliberately use only the RGB565-LE
/// formats (a small browse thumbnail + the 320×240 full-screen image): they are the simplest to
/// pack correctly and are sufficient for the iPod to both list and display the photo. The TV-out
/// formats (1019 UYVY / 1067 I420) are intentionally skipped.
/// </summary>
internal static class PhotoFormats
{
    // iPod 5G/5.5G (Video): 1036 50×41 browse, 1015 130×88 preview, 1024 320×240 full-screen.
    private static readonly PhotoFormat[] Video = { new(1036, 50, 41, false), new(1015, 130, 88, false), new(1024, 320, 240, true) };
    // iPod Classic (and 3G Nano): 1066 64×64 browse, 1024 320×240 full-screen.
    private static readonly PhotoFormat[] Classic = { new(1066, 64, 64, false), new(1024, 320, 240, true) };
    // iPod photo (4G colour): 1009 42×30 browse, 1015 130×88 preview (both RGB565-LE).
    private static readonly PhotoFormat[] PhotoColor = { new(1009, 42, 30, false), new(1015, 130, 88, true) };

    /// <summary>The formats to render for a new photo on this device.</summary>
    public static PhotoFormat[] For(IPodGeneration gen) => gen switch
    {
        IPodGeneration.Video => Video,
        IPodGeneration.Photo => PhotoColor,
        // Classic + every Nano share the QVGA RGB565-LE full-screen format; the 64×64 browse thumb is safe.
        _ => Classic,
    };

    /// <summary>All formats we know the slot dimensions of (so we can decode them for the on-screen grid).</summary>
    private static readonly Dictionary<int, PhotoFormat> Known =
        new[] { Video, Classic, PhotoColor }.SelectMany(a => a)
            .GroupBy(f => f.FormatId).ToDictionary(g => g.Key, g => g.First());

    public static PhotoFormat? Lookup(int formatId) => Known.TryGetValue(formatId, out var f) ? f : null;
}
