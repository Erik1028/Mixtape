using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>The parsed contents of a "Photo Database": its photos, albums, and the next free id.</summary>
internal sealed class PhotoDbModel
{
    public List<Photo> Photos = new();
    public List<PhotoAlbum> Albums = new();
    public uint MaxImageId;
    public List<string> Warnings = new();
}

/// <summary>
/// Reads and writes the iPod "Photo Database" (mhfd) binary format, byte-for-byte per libgpod's
/// <c>db-artwork-writer.c</c>: three datasets (1 = images, 2 = albums, 3 = formats), the exact
/// padded header sizes, and the mandatory constants (mhfd.unknown2 = 2, unknown_flag1 = 2,
/// mhsd 16-bit index, mhii.song_id = image_id + 2, UTF-16LE type-3 filename mhods, etc.).
///
/// The thumbnail pixel bytes live in separate <c>.ithmb</c> files; this class only handles the
/// database that indexes them. <see cref="PhotoLibrary"/> wires the two together.
/// </summary>
internal static class PhotoDb
{
    // ---- parse ----

    public static PhotoDbModel Parse(byte[] data)
    {
        var m = new PhotoDbModel();
        var r = new ChunkReader(data);
        if (data.Length < 0x84 || r.Tag(0) != "mhfd") { m.Warnings.Add("Not a Photo Database (no mhfd)."); return m; }

        uint mhfdLen = r.U32(0x04);
        uint datasetCount = r.U32(0x14);
        int off = (int)mhfdLen;
        for (uint i = 0; i < datasetCount && off + 16 <= data.Length; i++)
        {
            if (r.Tag(off) != "mhsd") break;
            uint hl = r.U32(off + 0x04), tl = r.U32(off + 0x08);
            if (hl < 12 || tl < hl || tl > (uint)(data.Length - off)) break;
            int index = r.U16(off + 0x0C);
            int listOff = off + (int)hl;
            try
            {
                if (index == 1) ParseImageList(r, data, listOff, off + (int)tl, m);
                else if (index == 2) ParseAlbumList(r, data, listOff, off + (int)tl, m);
                // index 3 (formats) is regenerated on write — no need to parse.
            }
            catch (Exception ex) { m.Warnings.Add($"dataset {index}: {ex.Message}"); }
            off += (int)tl;
        }
        m.MaxImageId = m.Photos.Count > 0 ? m.Photos.Max(p => p.ImageId) : 0;
        return m;
    }

    private static void ParseImageList(ChunkReader r, byte[] data, int listOff, int end, PhotoDbModel m)
    {
        if (r.Tag(listOff) != "mhli") return;
        uint hl = r.U32(listOff + 0x04), count = r.U32(listOff + 0x08);
        int p = listOff + (int)hl;
        for (uint k = 0; k < count && p + 16 <= end; k++)
        {
            if (r.Tag(p) != "mhii") break;
            uint mhl = r.U32(p + 0x04), tl = r.U32(p + 0x08);
            if (tl < mhl || p + tl > end) break;
            var raw = new byte[tl];
            Buffer.BlockCopy(data, p, raw, 0, (int)tl);
            var photo = new Photo
            {
                ImageId = r.U32(p + 0x10),
                Date = MacTime.ToDateTime(r.U32(p + 0x28)),
                OrigImageSize = r.U32(p + 0x30),
                RawMhii = raw, // preserved verbatim so existing photos write back bit-identical
            };
            uint nThumbs = r.U32(p + 0x0C);
            int cp = p + (int)mhl;
            for (uint t = 0; t < nThumbs && cp + 16 <= p + (int)tl; t++)
            {
                if (r.Tag(cp) != "mhod") break;
                uint mt = r.U32(cp + 0x08);
                if (mt < 12 || cp + mt > p + tl) break;
                if (r.U16(cp + 0x0C) == 2) // location wrapper → one mhni
                {
                    var thumb = ParseMhni(r, cp + (int)r.U32(cp + 0x04), cp + (int)mt);
                    if (thumb is not null) photo.Thumbs.Add(thumb);
                }
                cp += (int)mt;
            }
            m.Photos.Add(photo);
            p += (int)tl;
        }
    }

    private static PhotoThumb? ParseMhni(ChunkReader r, int off, int end)
    {
        if (off + 0x24 > end || r.Tag(off) != "mhni") return null;
        uint hl = r.U32(off + 0x04);
        var thumb = new PhotoThumb
        {
            FormatId = (int)r.U32(off + 0x10),
            Offset = r.U32(off + 0x14),
            Size = (int)r.U32(off + 0x18),
            VPad = r.U16(off + 0x1C),
            HPad = r.U16(off + 0x1E),
            ImageHeight = r.U16(off + 0x20),
            ImageWidth = r.U16(off + 0x22),
        };
        var fmt = PhotoFormats.Lookup(thumb.FormatId);
        if (fmt is not null) { thumb.SlotWidth = fmt.Width; thumb.SlotHeight = fmt.Height; }
        // child string mhod (type 3) → the .ithmb filename, from which we recover the file index.
        int cp = off + (int)hl;
        if (cp + 0x24 <= end && r.Tag(cp) == "mhod" && r.U16(cp + 0x0C) == 3)
        {
            int strLen = (int)r.U32(cp + 0x18);
            string path = r.U8(cp + 0x1C) == 2 ? r.Utf16(cp + 0x24, Math.Min(strLen, end - (cp + 0x24)))
                                                : r.Utf8(cp + 0x24, Math.Min(strLen, end - (cp + 0x24)));
            thumb.FileIndex = ParseFileIndex(path);
        }
        return thumb;
    }

    private static int ParseFileIndex(string mhodPath)
    {
        // ":Thumbs:F1024_3.ithmb" → 3
        int us = mhodPath.LastIndexOf('_');
        int dot = mhodPath.LastIndexOf('.');
        if (us >= 0 && dot > us && int.TryParse(mhodPath.AsSpan(us + 1, dot - us - 1), out int n)) return n;
        return 1;
    }

    private static void ParseAlbumList(ChunkReader r, byte[] data, int listOff, int end, PhotoDbModel m)
    {
        if (r.Tag(listOff) != "mhla") return;
        uint hl = r.U32(listOff + 0x04), count = r.U32(listOff + 0x08);
        int p = listOff + (int)hl;
        for (uint k = 0; k < count && p + 16 <= end; k++)
        {
            if (r.Tag(p) != "mhba") break;
            uint mhl = r.U32(p + 0x04), tl = r.U32(p + 0x08);
            if (tl < mhl || p + tl > end) break;
            var raw = new byte[tl];
            Buffer.BlockCopy(data, p, raw, 0, (int)tl);
            var album = new PhotoAlbum
            {
                AlbumId = r.U32(p + 0x14),
                IsMaster = r.U8(p + 0x1E) == 1,
                RawMhba = raw,
            };
            uint nMhods = r.U32(p + 0x0C), nMhias = r.U32(p + 0x10);
            int cp = p + (int)mhl;
            for (uint i = 0; i < nMhods && cp + 16 <= p + (int)tl; i++)
            {
                if (r.Tag(cp) != "mhod") break;
                uint mt = r.U32(cp + 0x08);
                if (mt < 12) break;
                if (r.U16(cp + 0x0C) == 1) // album-name string (UTF-8)
                {
                    int sl = (int)r.U32(cp + 0x18);
                    album.Name = r.Utf8(cp + 0x24, Math.Min(sl, (int)mt - 0x24));
                }
                cp += (int)mt;
            }
            for (uint i = 0; i < nMhias && cp + 16 <= p + (int)tl; i++)
            {
                if (r.Tag(cp) != "mhia") break;
                uint mt = r.U32(cp + 0x08);
                if (mt < 0x14) break;
                album.ImageIds.Add(r.U32(cp + 0x10));
                cp += (int)mt;
            }
            m.Albums.Add(album);
            p += (int)tl;
        }
    }

    // ---- build (bottom-up; each chunk's total_len computed from its children) ----

    /// <summary>
    /// Serialize the model to Photo Database bytes. Each thumb must already carry its assigned
    /// <see cref="PhotoThumb.Offset"/> / <see cref="PhotoThumb.FileIndex"/> from the .ithmb writer.
    /// A master "Photo Library" album (album_type 1) containing every photo is ensured.
    /// </summary>
    public static byte[] Build(PhotoDbModel m)
    {
        // Dataset 1 — images. Existing photos are written back verbatim (their raw mhii preserves
        // iTunes' dates/ratings/flags and their existing .ithmb offsets); only added photos are synthesized.
        var mhiis = m.Photos.Select(p => p.RawMhii ?? BuildMhii(p)).ToList();
        byte[] mhli = WithTotalListHeader("mhli", 0x5c, (uint)mhiis.Count, mhiis);
        byte[] mhsd1 = WrapMhsd(1, mhli);

        // Dataset 2 — albums. The master "library" album is rebuilt so it lists every photo (incl. new
        // ones); other albums are written back verbatim so their membership/settings are untouched.
        var albums = EnsureMasterAlbum(m);
        var mhbas = albums.Select(a => (!a.IsMaster && a.RawMhba is not null) ? a.RawMhba : BuildMhba(a)).ToList();
        byte[] mhla = WithTotalListHeader("mhla", 0x5c, (uint)mhbas.Count, mhbas);
        byte[] mhsd2 = WrapMhsd(2, mhla);

        // Dataset 3 — format descriptors (one mhif per distinct format actually used)
        var formats = m.Photos.SelectMany(p => p.Thumbs)
            .GroupBy(t => t.FormatId)
            .Select(g => (FormatId: g.Key, ImageSize: g.Max(t => t.Size)))
            .OrderBy(f => f.FormatId).ToList();
        var mhifs = formats.Select(f => BuildMhif(f.FormatId, f.ImageSize)).ToList();
        byte[] mhlf = WithTotalListHeader("mhlf", 0x5c, (uint)mhifs.Count, mhifs);
        byte[] mhsd3 = WrapMhsd(3, mhlf);

        // Root mhfd
        var children = new List<byte[]> { mhsd1, mhsd2, mhsd3 };
        byte[] mhfd = Hdr("mhfd", 0x84);
        // next_id must be ≥ every existing image id so the iPod never reissues a colliding id.
        // Compute it from the photos themselves rather than a cached counter that can go stale.
        uint maxId = m.Photos.Count > 0 ? m.Photos.Max(p => p.ImageId) : 0;
        PutU32(mhfd, 0x10, 2);                                   // unknown2 = 2 (mandatory)
        PutU32(mhfd, 0x14, 3);                                   // num_children = 3
        PutU32(mhfd, 0x1C, maxId + 1);                           // next_id
        mhfd[0x30] = 2;                                          // unknown_flag1 = 2 (mandatory)
        int total = mhfd.Length + children.Sum(c => c.Length);
        PutU32(mhfd, 0x08, (uint)total);
        return Concat(Prepend(mhfd, children));
    }

    private static List<PhotoAlbum> EnsureMasterAlbum(PhotoDbModel m)
    {
        var allIds = m.Photos.Select(p => p.ImageId).ToList();
        var master = m.Albums.FirstOrDefault(a => a.IsMaster);
        if (master is null)
        {
            master = new PhotoAlbum { Name = "Photo Library", IsMaster = true, AlbumId = 0 };
            m.Albums.Insert(0, master);
        }
        master.ImageIds = allIds; // the library always lists every photo
        return m.Albums;
    }

    private static byte[] BuildMhii(Photo p)
    {
        var thumbMhods = p.Thumbs.Select(BuildThumbMhod).ToList();
        byte[] h = Hdr("mhii", 0x98);
        PutU32(h, 0x0C, (uint)thumbMhods.Count);                 // num_children
        PutU32(h, 0x10, p.ImageId);
        PutU64(h, 0x14, (ulong)p.ImageId + 2);                   // song_id = image_id + 2 (self-ref)
        PutU32(h, 0x28, MacTime.FromDateTime(p.Date));           // orig_date
        PutU32(h, 0x2C, MacTime.FromDateTime(p.Date));           // digitized_date
        PutU32(h, 0x30, p.OrigImageSize);
        int total = h.Length + thumbMhods.Sum(c => c.Length);
        PutU32(h, 0x08, (uint)total);
        return Concat(Prepend(h, thumbMhods));
    }

    private static byte[] BuildThumbMhod(PhotoThumb t)
    {
        byte[] mhni = BuildMhni(t);
        byte[] h = Hdr("mhod", 0x18);                            // location wrapper
        PutU16(h, 0x0C, 2);                                      // type 2 = location
        PutU32(h, 0x08, (uint)(h.Length + mhni.Length));
        return Concat(h, mhni);
    }

    private static byte[] BuildMhni(PhotoThumb t)
    {
        byte[] str = BuildStringMhod(3, t.MhodPath, utf16: true); // type-3 filename
        byte[] h = Hdr("mhni", 0x4c);
        PutU32(h, 0x0C, 1);                                      // num_children = 1
        PutU32(h, 0x10, (uint)t.FormatId);
        PutU32(h, 0x14, (uint)t.Offset);
        PutU32(h, 0x18, (uint)t.Size);
        PutU16(h, 0x1C, (ushort)t.VPad);
        PutU16(h, 0x1E, (ushort)t.HPad);
        PutU16(h, 0x20, (ushort)t.ImageHeight);
        PutU16(h, 0x22, (ushort)t.ImageWidth);
        PutU32(h, 0x08, (uint)(h.Length + str.Length));
        return Concat(h, str);
    }

    private static byte[] BuildMhba(PhotoAlbum a)
    {
        byte[] name = BuildStringMhod(1, a.Name, utf16: false);  // album name, UTF-8
        var mhias = a.ImageIds.Select(BuildMhia).ToList();
        byte[] h = Hdr("mhba", 0x94);
        PutU32(h, 0x0C, 1);                                      // num_mhods = 1 (the name)
        PutU32(h, 0x10, (uint)a.ImageIds.Count);                 // num_mhias
        PutU32(h, 0x14, a.AlbumId);
        h[0x1E] = (byte)(a.IsMaster ? 1 : 2);                    // album_type
        var kids = new List<byte[]> { name };
        kids.AddRange(mhias);
        int total = h.Length + kids.Sum(c => c.Length);
        PutU32(h, 0x08, (uint)total);
        return Concat(Prepend(h, kids));
    }

    private static byte[] BuildMhia(uint imageId)
    {
        byte[] h = Hdr("mhia", 0x28);
        PutU32(h, 0x08, 0x28);                                   // total_len = header_len (no children)
        PutU32(h, 0x10, imageId);
        return h;
    }

    private static byte[] BuildMhif(int formatId, int imageSize)
    {
        byte[] h = Hdr("mhif", 0x7c);
        PutU32(h, 0x08, 0x7c);                                   // total_len = header_len
        PutU32(h, 0x10, (uint)formatId);
        PutU32(h, 0x14, (uint)imageSize);                        // width*height*2
        return h;
    }

    /// <summary>Build a type-1 (album name, UTF-8) or type-3 (filename, UTF-16LE) string mhod.</summary>
    private static byte[] BuildStringMhod(int type, string value, bool utf16)
    {
        byte[] strBytes = utf16 ? Encoding.Unicode.GetBytes(value) : Encoding.UTF8.GetBytes(value);
        const int fixedHeader = 0x24;
        int pad = (4 - ((fixedHeader + strBytes.Length) % 4)) % 4;
        var b = new byte[fixedHeader + strBytes.Length + pad];
        Encoding.ASCII.GetBytes("mhod", 0, 4, b, 0);
        PutU32(b, 0x04, 0x18);                                   // header_len = 0x18 (NOT 0x24)
        PutU32(b, 0x08, (uint)b.Length);                         // total_len
        PutU16(b, 0x0C, (ushort)type);
        b[0x0F] = (byte)pad;                                     // padding_len
        PutU32(b, 0x18, (uint)strBytes.Length);                  // string_len (bytes)
        b[0x1C] = (byte)(utf16 ? 2 : 1);                         // encoding (2 = UTF-16LE, 1 = UTF-8)
        strBytes.CopyTo(b, 0x24);
        return b;
    }

    // ---- low-level byte helpers ----

    private static byte[] WrapMhsd(int index, byte[] list)
    {
        byte[] h = Hdr("mhsd", 0x60);
        PutU16(h, 0x0C, (ushort)index);                          // 16-bit index (Photo/Artwork variant)
        PutU32(h, 0x08, (uint)(h.Length + list.Length));
        return Concat(h, list);
    }

    private static byte[] WithTotalListHeader(string tag, int headerLen, uint childCount, List<byte[]> children)
    {
        byte[] h = Hdr(tag, headerLen);
        PutU32(h, 0x08, childCount);                             // mhli/mhla/mhlf: off8 = CHILD COUNT (no total_len)
        return Concat(Prepend(h, children));
    }

    private static byte[] Hdr(string tag, int headerLen)
    {
        var b = new byte[headerLen];
        Encoding.ASCII.GetBytes(tag, 0, 4, b, 0);
        PutU32(b, 0x04, (uint)headerLen);
        return b;
    }

    private static void PutU16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off, 2), v);
    private static void PutU32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off, 4), v);
    private static void PutU64(byte[] b, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off, 8), v);

    private static List<byte[]> Prepend(byte[] head, List<byte[]> rest) { var l = new List<byte[]>(rest.Count + 1) { head }; l.AddRange(rest); return l; }

    private static byte[] Concat(params byte[][] parts) => Concat((IEnumerable<byte[]>)parts);
    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var list = parts as IList<byte[]> ?? parts.ToList();
        int total = list.Sum(p => p.Length);
        var r = new byte[total];
        int o = 0;
        foreach (var p in list) { Buffer.BlockCopy(p, 0, r, 0 + o, p.Length); o += p.Length; }
        return r;
    }
}
