namespace iPodCommander;

/// <summary>
/// Parses an iTunesDB byte buffer into an <see cref="ITunesDb"/>.
///
/// The walker is generic: every chunk is located by its 4-char tag, its header length
/// (@0x04) and the u32 at @0x08, which is a CHILD COUNT for list chunks and a TOTAL BYTE
/// LENGTH for everything else (see <see cref="ChunkTag.IsListChunk"/>). We never assume a
/// fixed header size — we always read the length field — so the same code reads old (Mini/
/// Nano) and newer (Classic) databases.
///
/// Every advance is validated: a chunk's total length must be at least its own header
/// (forward progress) and must fit inside the file (in-bounds, no int overflow). A list that
/// ends early records a note in <see cref="ITunesDb.Warnings"/> so a truncated/corrupt
/// database is distinguishable from a legitimately empty one rather than silently dropping
/// records. Primitive reads are bounds-checked by <see cref="ChunkReader"/>.
/// </summary>
internal static class ITunesDbReader
{
    public static ITunesDb ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static ITunesDb Read(byte[] data)
    {
        var r = new ChunkReader(data);
        var db = new ITunesDb();

        if (r.Length < 0x68 || r.Tag(0) != ChunkTag.Mhbd)
            throw new InvalidDataException("Not an iTunesDB: the file does not start with an 'mhbd' header.");

        uint mhbdHeaderLen = r.U32(0x04);
        if (mhbdHeaderLen < 16 || mhbdHeaderLen > (uint)r.Length)
            throw new InvalidDataException($"iTunesDB mhbd header length 0x{mhbdHeaderLen:X} is invalid for a {r.Length}-byte file.");
        db.HeaderLength = mhbdHeaderLen;
        db.Version = r.U32(0x10);
        uint datasetCount = r.U32(0x14);
        db.PersistentId = r.U64(0x48);

        int off = (int)mhbdHeaderLen;
        uint datasetsRead = 0;
        for (; datasetsRead < datasetCount && off + 16 <= r.Length; datasetsRead++)
        {
            if (r.Tag(off) != ChunkTag.Mhsd) break; // structure broken — stop gracefully
            uint mhsdHeaderLen = r.U32(off + 0x04);
            uint mhsdTotalLen = r.U32(off + 0x08);
            // The dataset must contain its own header and fit inside the file: this both
            // guarantees forward progress and stops a corrupt length from walking off the
            // end or wrapping negative when cast to int.
            if (mhsdHeaderLen < 12 || mhsdTotalLen < mhsdHeaderLen || mhsdTotalLen > (uint)(r.Length - off)) break;

            uint type = r.U32(off + 0x0C);
            int listOff = off + (int)mhsdHeaderLen;
            switch (type)
            {
                case 1: ReadTrackList(r, listOff, db); break;
                // mhsd type 2 (legacy) and type 3 (modern) are BOTH full playlist lists — iTunes writes the
                // same playlists in both for compatibility; type 3 is NOT a podcast-only dataset. The actual
                // Podcasts playlist is identified per-playlist by its mhyp podcast flag (@0x2A), read below —
                // NOT by the dataset type. (Flagging the whole type-3 set as podcast wrongly hid every modern
                // playlist from "Add to playlist", because the de-dup keeps the first-seen type-3 copy.)
                case 2:
                case 3: ReadPlaylistList(r, listOff, db); break;
                default: break; // 4=albums, 5=smart, 9=genius — not modeled in Milestone 1
            }

            off += (int)mhsdTotalLen;
        }

        if (datasetsRead < datasetCount)
            db.Warnings.Add($"Expected {datasetCount} datasets but read {datasetsRead}; the database may be truncated or unusual.");
        int masterCount = db.Playlists.Count(p => p.IsMaster);
        if (db.Playlists.Count > 0 && masterCount != 1)
            db.Warnings.Add($"Expected exactly one master playlist but found {masterCount}.");

        return db;
    }

    // --- tracks ------------------------------------------------------------

    private static void ReadTrackList(ChunkReader r, int off, ITunesDb db)
    {
        if (off + 12 > r.Length || r.Tag(off) != ChunkTag.Mhlt) return;
        uint headerLen = r.U32(off + 0x04);
        uint count = r.U32(off + 0x08); // LIST chunk: child count, not a byte length
        int p = off + (int)headerLen;
        uint read = 0;
        for (; read < count && p + 16 <= r.Length; read++)
        {
            if (r.Tag(p) != ChunkTag.Mhit) break;
            uint itemHeaderLen = r.U32(p + 0x04);
            uint total = r.U32(p + 0x08);
            if (total < itemHeaderLen || total > (uint)(r.Length - p)) break; // forward progress, in-bounds
            db.Tracks.Add(ReadTrack(r, p));
            p += (int)total;
        }
        if (read < count)
            db.Warnings.Add($"Track list declared {count} tracks but only {read} were readable.");
    }

    private static Track ReadTrack(ChunkReader r, int off)
    {
        uint headerLen = r.U32(off + 0x04);
        uint mhodCount = r.U32(off + 0x0C);

        var t = new Track
        {
            UniqueId = r.U32(off + 0x10),
            Rating = r.U8(off + 0x1F),
            FileSize = r.U32(off + 0x24),
            LengthMs = r.U32(off + 0x28),
            TrackNumber = r.U32(off + 0x2C),
            TotalTracks = r.U32(off + 0x30),
            Year = r.U32(off + 0x34),
            Bitrate = r.U32(off + 0x38),
            SampleRate = r.U32(off + 0x3C) >> 16, // Hz lives in the high 16 bits
            PlayCount = r.U32(off + 0x50),
            LastPlayed = MacTime.ToDateTime(r.U32(off + 0x58)),
            DiscNumber = r.U32(off + 0x5C),
            TotalDiscs = r.U32(off + 0x60),
            DateAdded = MacTime.ToDateTime(r.U32(off + 0x68)),
            Dbid = r.U64(off + 0x70),
        };
        // mediaType lives at 0xD0, only present on newer (longer) mhit headers.
        if (headerLen >= 0xD4) t.MediaType = r.U32(off + 0xD0);

        int p = off + (int)headerLen;
        for (uint i = 0; i < mhodCount && p + 16 <= r.Length; i++)
        {
            if (r.Tag(p) != ChunkTag.Mhod) break;
            uint mhodHeaderLen = r.U32(p + 0x04);
            uint total = r.U32(p + 0x08);
            if (total < mhodHeaderLen || total > (uint)(r.Length - p)) break;
            ApplyTrackMhod(r, p, t);
            p += (int)total;
        }
        return t;
    }

    private static void ApplyTrackMhod(ChunkReader r, int off, Track t)
    {
        uint type = r.U32(off + 0x0C);
        switch (type)
        {
            case 1: t.Title = ReadMhodString(r, off); break;
            case 2: t.Location = ReadMhodString(r, off); break;
            case 3: t.Album = ReadMhodString(r, off); break;
            case 4: t.Artist = ReadMhodString(r, off); break;
            case 5: t.Genre = ReadMhodString(r, off); break;
            case 6: t.FileTypeDescription = ReadMhodString(r, off); break;
            case 8: t.Comment = ReadMhodString(r, off); break;
            case 12: t.Composer = ReadMhodString(r, off); break;
            case 22: t.AlbumArtist = ReadMhodString(r, off); break;
            default: break; // sort keys, smart-playlist blobs, podcast URLs etc. — ignored in M1
        }
    }

    // --- playlists ---------------------------------------------------------

    private static void ReadPlaylistList(ChunkReader r, int off, ITunesDb db)
    {
        if (off + 12 > r.Length || r.Tag(off) != ChunkTag.Mhlp) return;
        uint headerLen = r.U32(off + 0x04);
        uint count = r.U32(off + 0x08); // #mhyp
        int p = off + (int)headerLen;
        uint read = 0;
        for (; read < count && p + 16 <= r.Length; read++)
        {
            if (r.Tag(p) != ChunkTag.Mhyp) break;
            uint itemHeaderLen = r.U32(p + 0x04);
            uint total = r.U32(p + 0x08);
            if (total < itemHeaderLen || total > (uint)(r.Length - p)) break;
            var pl = ReadPlaylist(r, p, db);
            db.Playlists.Add(pl);
            p += (int)total;
        }
        if (read < count)
            db.Warnings.Add($"Playlist list declared {count} playlists but only {read} were readable.");
    }

    private static Playlist ReadPlaylist(ChunkReader r, int off, ITunesDb db)
    {
        uint headerLen = r.U32(off + 0x04);
        uint mhodCount = r.U32(off + 0x0C);
        uint mhipCount = r.U32(off + 0x10);

        var pl = new Playlist
        {
            IsMaster = r.U8(off + 0x14) != 0,
            PersistentId = r.U64(off + 0x1C),
        };
        if (headerLen >= 0x2C) pl.IsPodcast |= r.U16(off + 0x2A) != 0;
        if (headerLen >= 0x30) pl.SortOrder = r.U32(off + 0x2C);

        int p = off + (int)headerLen;
        // The playlist's own mhods come first (name = type 1) ...
        for (uint i = 0; i < mhodCount && p + 16 <= r.Length; i++)
        {
            if (r.Tag(p) != ChunkTag.Mhod) break;
            uint mhodHeaderLen = r.U32(p + 0x04);
            uint total = r.U32(p + 0x08);
            if (total < mhodHeaderLen || total > (uint)(r.Length - p)) break;
            if (r.U32(p + 0x0C) == 1) pl.Name = ReadMhodString(r, p);
            p += (int)total;
        }
        // ... then the member items. Each mhip owns a child mhod (type 100) carrying its
        // ordinal position, but the items are already laid out in order, so advancing by
        // mhip.totalLength (which spans that child) preserves order for free.
        uint readItems = 0;
        for (; readItems < mhipCount && p + 16 <= r.Length; readItems++)
        {
            if (r.Tag(p) != ChunkTag.Mhip) break;
            uint itemHeaderLen = r.U32(p + 0x04);
            uint total = r.U32(p + 0x08);
            if (total < itemHeaderLen || total > (uint)(r.Length - p)) break;
            pl.TrackIds.Add(r.U32(p + 0x18)); // trackId == some Track.UniqueId
            p += (int)total;
        }
        if (readItems < mhipCount)
            db.Warnings.Add($"Playlist '{(string.IsNullOrEmpty(pl.Name) ? "(unnamed)" : pl.Name)}' declared {mhipCount} items but only {readItems} were readable.");
        return pl;
    }

    // --- shared ------------------------------------------------------------

    /// <summary>Reads a UTF-16LE string mhod body (byte length @0x1C, data @0x28), clamped to the chunk.</summary>
    private static string ReadMhodString(ChunkReader r, int off)
    {
        int total = (int)Math.Min(r.U32(off + 0x08), (uint)(r.Length - off));
        int maxLen = Math.Max(0, total - 0x28);
        int byteLen = (int)Math.Clamp((long)r.U32(off + 0x1C), 0, maxLen);
        if ((byteLen & 1) != 0) byteLen--; // UTF-16 code units are 2 bytes
        return r.Utf16(off + 0x28, byteLen);
    }
}
