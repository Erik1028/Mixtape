namespace iPodCommander;

/// <summary>
/// One photo in the iPod "Photo Database" (mhii). A photo is just an id, a date, and a set of
/// pre-rendered <see cref="PhotoThumb"/> slots (one per device image format). Photos are entirely
/// independent of the iTunesDB.
/// </summary>
internal sealed class Photo
{
    public uint ImageId;            // mhii.image_id (unique within the photo db)
    public DateTime? Date;          // mhii.orig_date
    public uint OrigImageSize;      // mhii.orig_img_size (bytes of the original; cosmetic)
    public List<PhotoThumb> Thumbs = new();

    /// <summary>
    /// The verbatim mhii chunk bytes for a photo read from the device. When present it is written
    /// back byte-identical (preserving iTunes' dates/ratings/flags and existing .ithmb offsets);
    /// it is null for photos we add, which are synthesized. New photos' thumbnail pixels are
    /// appended to the .ithmb files, never disturbing the offsets these raw chunks point at.
    /// </summary>
    public byte[]? RawMhii;
    public bool IsNew => RawMhii is null;

    /// <summary>The smallest thumbnail we can actually decode (RGB565), for the on-screen grid.</summary>
    public PhotoThumb? BrowseThumb =>
        Thumbs.Where(t => t.SlotWidth > 0 && t.Pixels.Length > 0)
              .OrderBy(t => t.SlotWidth * t.SlotHeight)
              .FirstOrDefault();
}

/// <summary>
/// One rendered thumbnail slot of a photo: which device format it is, where its raw pixels live
/// in the .ithmb, the rendered region inside the fixed slot, and (when loaded) the pixel bytes.
/// </summary>
internal sealed class PhotoThumb
{
    public int FormatId;            // mhni.format_id / correlation id → F<id>_<n>.ithmb
    public int SlotWidth, SlotHeight; // the format's fixed slot in pixels (0 if the format is unknown to us)
    public int ImageWidth, ImageHeight; // mhni.image_width/height = padding + rendered size
    public int HPad, VPad;          // mhni.horizontal/vertical padding
    public int FileIndex = 1;       // the _<n> in F<id>_<n>.ithmb
    public long Offset;             // mhni.ithmb_offset
    public int Size;                // mhni.image_size (bytes in the .ithmb)
    public byte[] Pixels = Array.Empty<byte>(); // raw slot bytes (RGB565-LE for the formats we write)

    public string IthmbFileName => $"F{FormatId}_{FileIndex}.ithmb";
    public string MhodPath => $":Thumbs:{IthmbFileName}"; // stored in the type-3 string mhod
}

/// <summary>A photo album (mhba). v1 maintains only the master "Photo Library" containing every photo.</summary>
internal sealed class PhotoAlbum
{
    public string Name = "";
    public uint AlbumId;
    public bool IsMaster;           // album_type == 1
    public List<uint> ImageIds = new();

    /// <summary>Verbatim mhba bytes (non-master albums are written back unchanged; the master is rebuilt to include new photos).</summary>
    public byte[]? RawMhba;
}
