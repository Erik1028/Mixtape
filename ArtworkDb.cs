using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>One track's cover art in the ArtworkDB: the artwork id, the link to the track (its 64-bit
/// DBID), and one thumbnail per format (e.g. 100×100 + 200×200 on a 5G).</summary>
internal sealed class ArtworkItem
{
    public uint Id;              // mhii id @0x10 (artwork id; mhfd.next_id tracks max+1)
    public ulong TrackDbid;      // mhii song_id @0x18 — MUST equal the track's mhit @0x70
    public uint SourceImageSize; // mhii @0x34 (size of the original image; 0 is accepted)
    public List<ArtworkThumb> Thumbs = new();
    public byte[]? RawMhii;      // existing entries preserved verbatim on rewrite
}

internal sealed class ArtworkThumb
{
    public int FormatId;         // mhni correlation_id (1028=100×100, 1029=200×200 on 5G)
    public uint Offset;          // byte offset of this slot within its .ithmb
    public int Size;             // image_size = width*height*2
    public int Width, Height;
    public int VPad, HPad;
    public int FileIndex = 1;    // F<FormatId>_<FileIndex>.ithmb
    public string MhodPath => $":iPod_Control:Artwork:F{FormatId}_{FileIndex}.ithmb";
}

internal sealed class ArtworkDbModel
{
    public List<ArtworkItem> Items = new();
    public uint MaxId;
    public List<string> Warnings = new();
}

/// <summary>
/// Reads and writes the iPod cover-art database <c>iPod_Control/Artwork/ArtworkDB</c> (mhfd), per
/// libgpod's <c>db-artwork-writer.c</c>. It is structurally close to the Photo Database (<see cref="PhotoDb"/>)
/// but with three deliberate differences that the firmware requires:
///   • the mhsd dataset TYPE at +0x0C is a 32-bit u32 (the Photo DB uses a 16-bit index),
///   • each mhii links to its TRACK by the 64-bit DBID written at mhii @0x18 (== iTunesDB mhit @0x70);
///     the Photo DB instead self-references with song_id = image_id + 2,
///   • the per-thumbnail wrapper mhod is TYPE 1 (ARTWORK), not type 2 (location).
/// Datasets: 1 = image list (mhli → mhii), 2 = album list (mhla, emitted empty), 3 = file/format list
/// (mhlf → mhif). The pixel bytes live in F1028_1.ithmb / F1029_1.ithmb (RGB565-LE, same packing as
/// <see cref="Ithmb"/>). No checksum/hash — unlike the iTunesDB.
/// </summary>
internal static class ArtworkDb
{
    // ---- parse (enough to round-trip + verify links; albums/formats are regenerated on write) ----

    public static ArtworkDbModel Parse(byte[] data)
    {
        var m = new ArtworkDbModel();
        var r = new ChunkReader(data);
        if (data.Length < 0x84 || r.Tag(0) != "mhfd") { m.Warnings.Add("Not an ArtworkDB (no mhfd)."); return m; }

        uint mhfdLen = r.U32(0x04);
        uint datasetCount = r.U32(0x14);
        int off = (int)mhfdLen;
        for (uint i = 0; i < datasetCount && off + 16 <= data.Length; i++)
        {
            if (r.Tag(off) != "mhsd") break;
            uint hl = r.U32(off + 0x04), tl = r.U32(off + 0x08);
            if (hl < 12 || tl < hl || tl > (uint)(data.Length - off)) break;
            uint type = r.U32(off + 0x0C);            // 32-bit type (ArtworkDB variant)
            int listOff = off + (int)hl;
            try { if (type == 1) ParseImageList(r, data, listOff, off + (int)tl, m); }
            catch (Exception ex) { m.Warnings.Add($"dataset {type}: {ex.Message}"); }
            off += (int)tl;
        }
        m.MaxId = m.Items.Count > 0 ? m.Items.Max(it => it.Id) : 0;
        return m;
    }

    private static void ParseImageList(ChunkReader r, byte[] data, int listOff, int end, ArtworkDbModel m)
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
            var item = new ArtworkItem
            {
                Id = r.U32(p + 0x10),
                TrackDbid = r.U64(p + 0x18),
                SourceImageSize = r.U32(p + 0x34),
                RawMhii = raw,
            };
            uint nMhods = r.U32(p + 0x0C);
            int cp = p + (int)mhl;
            for (uint t = 0; t < nMhods && cp + 16 <= p + (int)tl; t++)
            {
                if (r.Tag(cp) != "mhod") break;
                uint mt = r.U32(cp + 0x08);
                if (mt < 12 || cp + mt > p + tl) break;
                if (r.U16(cp + 0x0C) == 1) // ARTWORK container → one mhni
                {
                    var thumb = ParseMhni(r, cp + (int)r.U32(cp + 0x04), cp + (int)mt);
                    if (thumb is not null) item.Thumbs.Add(thumb);
                }
                cp += (int)mt;
            }
            m.Items.Add(item);
            p += (int)tl;
        }
    }

    private static ArtworkThumb? ParseMhni(ChunkReader r, int off, int end)
    {
        if (off + 0x24 > end || r.Tag(off) != "mhni") return null;
        uint hl = r.U32(off + 0x04);
        var thumb = new ArtworkThumb
        {
            FormatId = (int)r.U32(off + 0x10),
            Offset = r.U32(off + 0x14),
            Size = (int)r.U32(off + 0x18),
            VPad = r.U16(off + 0x1C),
            HPad = r.U16(off + 0x1E),
            Height = r.U16(off + 0x20),
            Width = r.U16(off + 0x22),
        };
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
        int us = mhodPath.LastIndexOf('_');
        int dot = mhodPath.LastIndexOf('.');
        if (us >= 0 && dot > us && int.TryParse(mhodPath.AsSpan(us + 1, dot - us - 1), out int n)) return n;
        return 1;
    }

    // ---- build (bottom-up; total_len from children) ----

    public static byte[] Build(ArtworkDbModel m)
    {
        // Dataset 1 — images (existing entries verbatim; only new ones synthesized).
        var mhiis = m.Items.Select(it => it.RawMhii ?? BuildMhii(it)).ToList();
        byte[] mhli = WithTotalListHeader("mhli", 0x5c, (uint)mhiis.Count, mhiis);
        byte[] mhsd1 = WrapMhsd(1, mhli);

        // Dataset 2 — albums (emitted empty: per-track cover art needs no album records).
        byte[] mhla = WithTotalListHeader("mhla", 0x5c, 0, new List<byte[]>());
        byte[] mhsd2 = WrapMhsd(2, mhla);

        // Dataset 3 — one mhif per distinct format actually used.
        var formats = m.Items.SelectMany(it => it.Thumbs)
            .GroupBy(t => t.FormatId)
            .Select(g => (FormatId: g.Key, ImageSize: g.Max(t => t.Size)))
            .OrderBy(f => f.FormatId).ToList();
        var mhifs = formats.Select(f => BuildMhif(f.FormatId, f.ImageSize)).ToList();
        byte[] mhlf = WithTotalListHeader("mhlf", 0x5c, (uint)mhifs.Count, mhifs);
        byte[] mhsd3 = WrapMhsd(3, mhlf);

        var children = new List<byte[]> { mhsd1, mhsd2, mhsd3 };
        byte[] mhfd = Hdr("mhfd", 0x84);
        uint maxId = m.Items.Count > 0 ? m.Items.Max(it => it.Id) : 0;
        PutU32(mhfd, 0x10, 2);               // unknown2 = 2 (mandatory)
        PutU32(mhfd, 0x14, 3);               // num_children = 3 datasets
        PutU32(mhfd, 0x1C, maxId + 1);       // next_id
        mhfd[0x30] = 2;                       // unknown_flag1 = 2 (mandatory)
        PutU32(mhfd, 0x08, (uint)(mhfd.Length + children.Sum(c => c.Length)));
        return Concat(Prepend(mhfd, children));
    }

    private static byte[] BuildMhii(ArtworkItem it)
    {
        var mhods = it.Thumbs.Select(BuildArtworkMhod).ToList();
        byte[] h = Hdr("mhii", 0x98);
        PutU32(h, 0x0C, (uint)mhods.Count);          // num_children
        PutU32(h, 0x10, it.Id);                       // artwork id
        PutU64(h, 0x18, it.TrackDbid);                // song_id = track DBID (the link)
        PutU32(h, 0x34, it.SourceImageSize);          // source_image_size
        PutU32(h, 0x08, (uint)(h.Length + mhods.Sum(c => c.Length)));
        return Concat(Prepend(h, mhods));
    }

    private static byte[] BuildArtworkMhod(ArtworkThumb t)
    {
        byte[] mhni = BuildMhni(t);
        byte[] h = Hdr("mhod", 0x18);
        PutU16(h, 0x0C, 1);                            // type 1 = ARTWORK container (Photo DB uses 2)
        PutU32(h, 0x08, (uint)(h.Length + mhni.Length));
        return Concat(h, mhni);
    }

    private static byte[] BuildMhni(ArtworkThumb t)
    {
        byte[] str = BuildStringMhod(3, t.MhodPath, utf16: true);
        byte[] h = Hdr("mhni", 0x4c);
        PutU32(h, 0x0C, 1);                            // num_children = 1 (filename mhod)
        PutU32(h, 0x10, (uint)t.FormatId);             // correlation_id
        PutU32(h, 0x14, t.Offset);
        PutU32(h, 0x18, (uint)t.Size);
        PutU16(h, 0x1C, (ushort)t.VPad);
        PutU16(h, 0x1E, (ushort)t.HPad);
        PutU16(h, 0x20, (ushort)t.Height);
        PutU16(h, 0x22, (ushort)t.Width);
        PutU32(h, 0x08, (uint)(h.Length + str.Length));
        return Concat(h, str);
    }

    private static byte[] BuildMhif(int formatId, int imageSize)
    {
        byte[] h = Hdr("mhif", 0x7c);
        PutU32(h, 0x08, 0x7c);
        PutU32(h, 0x10, (uint)formatId);
        PutU32(h, 0x14, (uint)imageSize);
        return h;
    }

    private static byte[] BuildStringMhod(int type, string value, bool utf16)
    {
        byte[] strBytes = utf16 ? Encoding.Unicode.GetBytes(value) : Encoding.UTF8.GetBytes(value);
        const int fixedHeader = 0x24;
        int pad = (4 - ((fixedHeader + strBytes.Length) % 4)) % 4;
        var b = new byte[fixedHeader + strBytes.Length + pad];
        Encoding.ASCII.GetBytes("mhod", 0, 4, b, 0);
        PutU32(b, 0x04, 0x18);
        PutU32(b, 0x08, (uint)b.Length);
        PutU16(b, 0x0C, (ushort)type);
        b[0x0F] = (byte)pad;
        PutU32(b, 0x18, (uint)strBytes.Length);
        b[0x1C] = (byte)(utf16 ? 2 : 1);
        strBytes.CopyTo(b, 0x24);
        return b;
    }

    // ---- low-level helpers ----

    private static byte[] WrapMhsd(uint type, byte[] list)
    {
        byte[] h = Hdr("mhsd", 0x60);
        PutU32(h, 0x0C, type);                         // 32-bit type (ArtworkDB variant)
        PutU32(h, 0x08, (uint)(h.Length + list.Length));
        return Concat(h, list);
    }

    private static byte[] WithTotalListHeader(string tag, int headerLen, uint childCount, List<byte[]> children)
    {
        byte[] h = Hdr(tag, headerLen);
        PutU32(h, 0x08, childCount);                    // mhli/mhla/mhlf: off8 = CHILD COUNT
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
        var r = new byte[list.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in list) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
