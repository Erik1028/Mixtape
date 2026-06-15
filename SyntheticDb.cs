using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Builds a tiny but structurally-valid iTunesDB in memory: an mhbd holding a track dataset
/// (2 tracks) and a playlist dataset (a master list + one ordinary playlist). It exists so
/// Milestone 1 can validate the reader (and exercise <see cref="ChunkWriter"/>) WITHOUT a
/// device — the riskiest code, the chunk walker, is proven before it ever touches an iPod.
///
/// Container totals are back-patched from the writer position after children are emitted, so
/// the length accounting here is the same discipline the real writer will use in Milestone 2.
/// </summary>
internal static class SyntheticDb
{
    public static byte[] Build()
    {
        var w = new ChunkWriter();

        // ---- mhbd (root) ----
        int mhbd = w.Position;
        var mhbdHdr = NewHeader("mhbd", 0x68);
        PutU32(mhbdHdr, 0x0C, 1);        // unknown1
        PutU32(mhbdHdr, 0x10, 0x13);     // version
        PutU32(mhbdHdr, 0x14, 2);        // child datasets: tracks + playlists
        PutU64(mhbdHdr, 0x48, 0xABCDEF0123456789UL); // db persistent id
        w.Bytes(mhbdHdr);

        // ---- dataset 1: tracks ----
        int mhsd1 = w.Position;
        var mhsd1Hdr = NewHeader("mhsd", 0x60);
        PutU32(mhsd1Hdr, 0x0C, 1);       // type 1 = tracks
        w.Bytes(mhsd1Hdr);

        var mhltHdr = NewHeader("mhlt", 0x5C);
        PutU32(mhltHdr, 0x08, 2);        // LIST chunk: child count (#mhit), NOT a byte length
        w.Bytes(mhltHdr);

        WriteTrack(w, uniqueId: 101, dbid: 0x1111_1111_1111_1111UL,
            title: "Bohemian Rhapsody", artist: "Queen", album: "A Night at the Opera",
            genre: "Rock", location: ":iPod_Control:Music:F00:AAAA.mp3",
            lengthMs: 354000, fileSize: 8_500_000, bitrate: 192, sampleRate: 44100, year: 1975, trackNo: 11);

        WriteTrack(w, uniqueId: 102, dbid: 0x2222_2222_2222_2222UL,
            title: "Imagine", artist: "John Lennon", album: "Imagine",
            genre: "Rock", location: ":iPod_Control:Music:F01:BBBB.mp3",
            lengthMs: 183000, fileSize: 4_400_000, bitrate: 160, sampleRate: 44100, year: 1971, trackNo: 1);

        w.PatchU32(mhsd1 + 0x08, (uint)(w.Position - mhsd1)); // mhsd total = header + mhlt + mhits

        // ---- dataset 2: playlists ----
        int mhsd2 = w.Position;
        var mhsd2Hdr = NewHeader("mhsd", 0x60);
        PutU32(mhsd2Hdr, 0x0C, 2);       // type 2 = playlists
        w.Bytes(mhsd2Hdr);

        var mhlpHdr = NewHeader("mhlp", 0x5C);
        PutU32(mhlpHdr, 0x08, 2);        // LIST chunk: #mhyp
        w.Bytes(mhlpHdr);

        WritePlaylist(w, name: "iPod", isMaster: true, persistentId: 0x9000, trackIds: new uint[] { 101, 102 });
        WritePlaylist(w, name: "Favourites", isMaster: false, persistentId: 0x9001, trackIds: new uint[] { 102 }, sortOrder: 5);

        w.PatchU32(mhsd2 + 0x08, (uint)(w.Position - mhsd2));

        // ---- finalise root total ----
        w.PatchU32(mhbd + 0x08, (uint)(w.Position - mhbd));
        return w.ToArray();
    }

    private static void WriteTrack(ChunkWriter w, uint uniqueId, ulong dbid, string title, string artist,
        string album, string genre, string location, uint lengthMs, uint fileSize, uint bitrate,
        uint sampleRate, uint year, uint trackNo)
    {
        int start = w.Position;
        var h = NewHeader("mhit", 0xF4);
        PutU32(h, 0x0C, 6);                       // mhod child count (title, artist, album, genre, location, filetype)
        PutU32(h, 0x10, uniqueId);
        PutU32(h, 0x14, 1);                        // visible
        PutU32(h, 0x24, fileSize);
        PutU32(h, 0x28, lengthMs);
        PutU32(h, 0x2C, trackNo);
        PutU32(h, 0x34, year);
        PutU32(h, 0x38, bitrate);
        PutU32(h, 0x3C, sampleRate << 16);         // Hz lives in the high 16 bits
        PutU64(h, 0x70, dbid);
        PutU32(h, 0xD0, 1);                        // mediaType = audio
        w.Bytes(h);

        WriteStringMhod(w, 1, title);
        WriteStringMhod(w, 4, artist);
        WriteStringMhod(w, 3, album);
        WriteStringMhod(w, 5, genre);
        WriteStringMhod(w, 2, location);
        WriteStringMhod(w, 6, "MPEG audio file");

        w.PatchU32(start + 0x08, (uint)(w.Position - start));
    }

    private static void WritePlaylist(ChunkWriter w, string name, bool isMaster, ulong persistentId, uint[] trackIds, uint sortOrder = 0)
    {
        int start = w.Position;
        var h = NewHeader("mhyp", 0x6C);
        PutU32(h, 0x0C, 1);                        // one name mhod
        PutU32(h, 0x10, (uint)trackIds.Length);    // mhip item count
        h[0x14] = (byte)(isMaster ? 1 : 0);
        PutU64(h, 0x1C, persistentId);
        PutU32(h, 0x2C, sortOrder);                // exercises the reader's headerLen>=0x30 sortOrder path
        w.Bytes(h);

        WriteStringMhod(w, 1, name);               // playlist name

        foreach (uint id in trackIds)
            WritePlaylistItem(w, id);

        w.PatchU32(start + 0x08, (uint)(w.Position - start));
    }

    private static void WritePlaylistItem(ChunkWriter w, uint trackId)
    {
        int start = w.Position;
        var h = NewHeader("mhip", 0x4C);
        PutU32(h, 0x0C, 1);                        // one child mhod (position, type 100)
        PutU32(h, 0x18, trackId);
        w.Bytes(h);

        // child mhod type 100 (position) — empty body; the reader skips over it via mhip total.
        var pos = NewHeader("mhod", 0x18);
        PutU32(pos, 0x08, 0x28);                   // total length
        PutU32(pos, 0x0C, 100);
        Array.Resize(ref pos, 0x28);               // pad body to 0x28
        w.Bytes(pos);

        w.PatchU32(start + 0x08, (uint)(w.Position - start));
    }

    private static void WriteStringMhod(ChunkWriter w, uint type, string value)
    {
        byte[] s = Encoding.Unicode.GetBytes(value);
        var b = new byte[0x28 + s.Length];
        PutTag(b, "mhod");
        PutU32(b, 0x04, 0x18);                     // header length
        PutU32(b, 0x08, (uint)b.Length);           // total length
        PutU32(b, 0x0C, type);
        PutU32(b, 0x18, 1);                         // position / encoding marker
        PutU32(b, 0x1C, (uint)s.Length);            // string byte length (code units * 2)
        PutU32(b, 0x20, 1);
        s.CopyTo(b, 0x28);
        w.Bytes(b);
    }

    // --- byte helpers ---
    private static byte[] NewHeader(string tag, int headerLen)
    {
        var b = new byte[headerLen];
        PutTag(b, tag);
        PutU32(b, 0x04, (uint)headerLen);
        return b;
    }

    private static void PutTag(byte[] b, string tag) => Encoding.ASCII.GetBytes(tag, 0, 4, b, 0);
    private static void PutU32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off, 4), v);
    private static void PutU64(byte[] b, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off, 8), v);
}
