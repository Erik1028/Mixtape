using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>
/// A byte-preserving, editable representation of an iTunesDB, used for WRITING (Milestone 2).
///
/// Unlike <see cref="ITunesDbReader"/> (which extracts semantic fields for display), this keeps
/// the raw bytes of every chunk it doesn't need to change: whole datasets we don't model
/// (albums/smart/genius), each track's mhit (+ its mhods), each playlist's header/name block,
/// and each mhip. Editing means inserting/removing those raw blocks and letting
/// <see cref="Serialize"/> recompute only the parent length/count fields bottom-up.
///
/// The safety contract: <c>Serialize(Parse(x)) == x</c> byte-for-byte when nothing is edited.
/// That identity is proven against the real device DB by the <c>--roundtrip</c> self-test
/// before any mutation is trusted. Trailing/slack bytes inside every container are captured so
/// the reconstruction is exact even if a writer left padding.
/// </summary>
internal sealed class RawDb
{
    public byte[] MhbdHeader = Array.Empty<byte>();
    public List<RawDataset> Datasets = new();
    public byte[] Trailing = Array.Empty<byte>();
    public uint Version;

    // ---- parse ----

    public static RawDb Parse(byte[] data)
    {
        var r = new ChunkReader(data);
        if (data.Length < 0x68 || r.Tag(0) != ChunkTag.Mhbd)
            throw new InvalidDataException("Not an iTunesDB: missing mhbd header.");

        uint mhbdHeaderLen = r.U32(0x04);
        if (mhbdHeaderLen < 16 || mhbdHeaderLen > (uint)data.Length)
            throw new InvalidDataException($"mhbd header length 0x{mhbdHeaderLen:X} invalid.");

        var db = new RawDb
        {
            MhbdHeader = Slice(data, 0, (int)mhbdHeaderLen),
            Version = r.U32(0x10),
        };

        uint datasetCount = r.U32(0x14);
        int off = (int)mhbdHeaderLen;
        for (uint i = 0; i < datasetCount && off + 16 <= data.Length; i++)
        {
            if (r.Tag(off) != ChunkTag.Mhsd) break;
            uint hl = r.U32(off + 0x04), tl = r.U32(off + 0x08), type = r.U32(off + 0x0C);
            if (hl < 12 || tl < hl || tl > (uint)(data.Length - off)) break;

            int dsStart = off, dsLen = (int)tl, mhsdHl = (int)hl;
            db.Datasets.Add(type switch
            {
                1 => ParseTrackList(data, r, dsStart, mhsdHl, dsLen),
                2 or 3 => ParsePlaylistList(data, r, dsStart, mhsdHl, dsLen, type),
                _ => new RawDataset { Type = type, Verbatim = Slice(data, dsStart, dsLen) },
            });
            off += dsLen;
        }

        db.Trailing = off < data.Length ? Slice(data, off, data.Length - off) : Array.Empty<byte>();
        return db;
    }

    private static RawDataset ParseTrackList(byte[] data, ChunkReader r, int dsStart, int mhsdHl, int dsLen)
    {
        var ds = new RawDataset { Type = 1, MhsdHeader = Slice(data, dsStart, mhsdHl), Tracks = new() };
        int dsEnd = dsStart + dsLen;
        int listOff = dsStart + mhsdHl;
        int listHl = (int)r.U32(listOff + 0x04);
        uint count = r.U32(listOff + 0x08);
        ds.ListHeader = Slice(data, listOff, listHl);

        int p = listOff + listHl;
        for (uint k = 0; k < count && p + 16 <= dsEnd; k++)
        {
            if (r.Tag(p) != ChunkTag.Mhit) break;
            uint t = r.U32(p + 0x08);
            if (t < r.U32(p + 0x04) || t > (uint)(dsEnd - p)) break;
            ds.Tracks!.Add(Slice(data, p, (int)t));
            p += (int)t;
        }
        ds.Trailing = p < dsEnd ? Slice(data, p, dsEnd - p) : Array.Empty<byte>();
        return ds;
    }

    private static RawDataset ParsePlaylistList(byte[] data, ChunkReader r, int dsStart, int mhsdHl, int dsLen, uint type)
    {
        var ds = new RawDataset { Type = type, MhsdHeader = Slice(data, dsStart, mhsdHl), Playlists = new() };
        int dsEnd = dsStart + dsLen;
        int listOff = dsStart + mhsdHl;
        int listHl = (int)r.U32(listOff + 0x04);
        uint count = r.U32(listOff + 0x08);
        ds.ListHeader = Slice(data, listOff, listHl);

        int p = listOff + listHl;
        for (uint k = 0; k < count && p + 16 <= dsEnd; k++)
        {
            if (r.Tag(p) != ChunkTag.Mhyp) break;
            uint t = r.U32(p + 0x08);
            if (t < r.U32(p + 0x04) || t > (uint)(dsEnd - p)) break;
            ds.Playlists!.Add(ParsePlaylist(data, r, p, (int)t));
            p += (int)t;
        }
        ds.Trailing = p < dsEnd ? Slice(data, p, dsEnd - p) : Array.Empty<byte>();
        return ds;
    }

    private static RawPlaylist ParsePlaylist(byte[] data, ChunkReader r, int mhypStart, int mhypTotal)
    {
        int end = mhypStart + mhypTotal;
        int hl = (int)r.U32(mhypStart + 0x04);
        uint mhodCount = r.U32(mhypStart + 0x0C);
        uint mhipCount = r.U32(mhypStart + 0x10);

        // The prefix is the mhyp header + its name/settings mhods, i.e. everything up to the
        // first mhip. We keep it verbatim and only ever re-patch its count/length fields.
        int p = mhypStart + hl;
        for (uint k = 0; k < mhodCount && p + 16 <= end; k++)
        {
            if (r.Tag(p) != ChunkTag.Mhod) break;
            uint t = r.U32(p + 0x08);
            if (t < r.U32(p + 0x04) || t > (uint)(end - p)) break;
            p += (int)t;
        }

        var pl = new RawPlaylist { Prefix = Slice(data, mhypStart, p - mhypStart), Mhips = new() };
        for (uint k = 0; k < mhipCount && p + 16 <= end; k++)
        {
            if (r.Tag(p) != ChunkTag.Mhip) break;
            uint t = r.U32(p + 0x08);
            if (t < r.U32(p + 0x04) || t > (uint)(end - p)) break;
            pl.Mhips.Add(Slice(data, p, (int)t));
            p += (int)t;
        }
        pl.Trailing = p < end ? Slice(data, p, end - p) : Array.Empty<byte>();
        return pl;
    }

    // ---- edits ----

    /// <summary>Largest existing mhit uniqueId across the track dataset (0 if none).</summary>
    public uint MaxUniqueId()
    {
        var tracks = Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks;
        uint max = 0;
        if (tracks is not null)
            foreach (var mhit in tracks) max = Math.Max(max, ReadU32(mhit, 0x10));
        return max;
    }

    public int TrackCount => Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks?.Count ?? 0;

    /// <summary>
    /// Append a new track: add its mhit to the track dataset and a member mhip to the master
    /// playlist of every playlist dataset (this DB carries the playlists in both type 2 and
    /// type 3, so we keep them consistent). The caller owns assigning a unique <paramref name="uniqueId"/>.
    /// </summary>
    public void AddTrack(byte[] mhitChunk, uint uniqueId)
    {
        var tracks = Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks
            ?? throw new InvalidOperationException("No track dataset to add to.");
        tracks.Add(mhitChunk);

        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
        {
            // Add the member ONLY to a genuine master playlist (isMaster flag). This keeps a duplicated
            // master (type-2 + type-3) in sync, but never pollutes a podcasts dataset whose first
            // playlist is the podcast list, not the song library (no fallback to FirstOrDefault).
            var master = ds.Playlists!.FirstOrDefault(IsMaster);
            master?.Mhips.Add(master.Mhips.Count > 0 ? CloneMhip(master.Mhips[0], uniqueId) : BuildMinimalMhip(uniqueId));
        }
    }

    /// <summary>Remove a track everywhere: its mhit and every mhip (in every playlist) that references it.</summary>
    public bool RemoveTrack(uint uniqueId)
    {
        var tracks = Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks;
        if (tracks is null) return false;
        int removed = tracks.RemoveAll(mhit => ReadU32(mhit, 0x10) == uniqueId);
        if (removed == 0) return false;

        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!)
                pl.Mhips.RemoveAll(mhip => ReadU32(mhip, 0x18) == uniqueId);
        return true;
    }

    /// <summary>
    /// Edit a track's metadata in place: numeric fields (rating @0x1F, year @0x34, track# @0x2C) are
    /// patched directly in the mhit header; the managed string mhods (title=1, artist=4, album=3,
    /// genre=5, album-artist=22) are replaced/added/cleared while EVERY other mhod (location, file
    /// type, sort keys, smart-playlist blobs…) and any header field we don't touch is preserved
    /// verbatim. Returns false if the track id isn't found or the chunk is malformed (then unchanged).
    /// </summary>
    public bool EditTrack(uint uniqueId, TrackEdit edit)
    {
        var tracks = Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks;
        if (tracks is null) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Length < 0x20 || ReadU32(tracks[i], 0x10) != uniqueId) continue;
            var rebuilt = RebuildTrack(tracks[i], edit);
            if (ReferenceEquals(rebuilt, tracks[i])) return false; // malformed → left unchanged
            tracks[i] = rebuilt;
            return true;
        }
        return false;
    }

    private static byte[] RebuildTrack(byte[] raw, TrackEdit e)
    {
        int headerLen = (int)ReadU32(raw, 0x04);
        if (headerLen < 0x20 || headerLen > raw.Length) return raw; // malformed → caller leaves it alone
        uint mhodCount = ReadU32(raw, 0x0C);
        var header = Slice(raw, 0, headerLen);

        // numeric fields — fixed-width, no resize (only patch when the field fits this header)
        if (e.Rating is byte rt && headerLen > 0x1F) header[0x1F] = rt;
        if (e.Year is uint yr && headerLen >= 0x38) PatchU32(header, 0x34, yr);
        if (e.TrackNumber is uint tn && headerLen >= 0x30) PatchU32(header, 0x2C, tn);

        // managed string mhod types → new value ("" means clear/remove); null entries aren't added.
        var managed = new Dictionary<uint, string>();
        void M(uint type, string? v) { if (v is not null) managed[type] = v; }
        M(1, e.Title); M(4, e.Artist); M(3, e.Album); M(5, e.Genre); M(22, e.AlbumArtist);

        var bodies = new List<byte[]>();
        var seen = new HashSet<uint>();
        int p = headerLen;
        for (uint k = 0; k < mhodCount; k++)
        {
            if (p + 16 > raw.Length) return raw;                            // truncated → bail, change nothing
            uint total = ReadU32(raw, p + 0x08);
            if (total < 12 || p + (long)total > raw.Length) return raw;     // malformed → bail
            uint type = ReadU32(raw, p + 0x0C);
            if (managed.TryGetValue(type, out var val))
            {
                seen.Add(type);
                if (val.Length > 0) bodies.Add(BuildStringMhod(type, val)); // replace (drop if cleared to "")
            }
            else bodies.Add(Slice(raw, p, (int)total));                     // keep every other mhod verbatim
            p += (int)total;
        }
        byte[] slack = p < raw.Length ? Slice(raw, p, raw.Length - p) : Array.Empty<byte>(); // non-mhod trailing bytes

        // add managed tags that didn't exist before (a field set where the track had none)
        foreach (var kv in managed)
            if (!seen.Contains(kv.Key) && kv.Value.Length > 0) bodies.Add(BuildStringMhod(kv.Key, kv.Value));

        PatchU32(header, 0x0C, (uint)bodies.Count);
        var all = new List<byte[]> { header };
        all.AddRange(bodies);
        if (slack.Length > 0) all.Add(slack);
        PatchU32(header, 0x08, (uint)all.Sum(b => b.Length));
        return Concat(all);
    }

    // ---- playlist-level edits (these NEVER touch the track dataset, so songs are kept) ----

    private static ulong PlaylistPid(RawPlaylist pl) => pl.Prefix.Length >= 0x24 ? ReadU64(pl.Prefix, 0x1C) : 0;

    /// <summary>Delete a non-master playlist by persistent id from every playlist dataset; the songs stay in the library.</summary>
    public bool RemovePlaylist(ulong persistentId)
    {
        if (persistentId == 0) return false; // pid 0 is ambiguous (also coerced from short prefixes) → never edit by it
        bool removed = false;
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            removed |= ds.Playlists!.RemoveAll(p => !IsMaster(p) && PlaylistPid(p) == persistentId) > 0;
        return removed;
    }

    /// <summary>Remove specific tracks from one playlist (every dataset copy) while keeping them in the library.</summary>
    public bool RemoveTracksFromPlaylist(ulong persistentId, HashSet<uint> trackIds)
    {
        if (persistentId == 0) return false;
        bool changed = false;
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!.Where(p => !IsMaster(p) && PlaylistPid(p) == persistentId))
                changed |= pl.Mhips.RemoveAll(m => trackIds.Contains(ReadU32(m, 0x18))) > 0;
        return changed;
    }

    /// <summary>
    /// Reorder a playlist's members to match <paramref name="order"/> (a list of track unique-ids), in every
    /// dataset copy. The on-disk mhip SEQUENCE is the playlist order the iPod plays, so this just re-sorts the
    /// mhip blocks; ids not in <paramref name="order"/> keep their relative order at the end (stable sort).
    /// </summary>
    public bool ReorderPlaylist(ulong persistentId, IList<uint> order)
    {
        if (persistentId == 0) return false;
        var rank = new Dictionary<uint, int>();
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        bool changed = false;
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!.Where(p => !IsMaster(p) && PlaylistPid(p) == persistentId))
            {
                pl.Mhips = pl.Mhips.OrderBy(m => rank.TryGetValue(ReadU32(m, 0x18), out int r) ? r : int.MaxValue).ToList();
                changed = true;
            }
        return changed;
    }

    /// <summary>Rename a non-master playlist (its type-1 name mhod) in every dataset copy.</summary>
    public bool RenamePlaylist(ulong persistentId, string newName)
    {
        if (persistentId == 0) return false;
        bool changed = false;
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!.Where(p => PlaylistPid(p) == persistentId && !IsMaster(p)))
            {
                pl.Prefix = RebuildPrefixWithName(pl.Prefix, newName);
                changed = true;
            }
        return changed;
    }

    /// <summary>
    /// Create a new empty playlist (added to every playlist dataset so the type2/type3 copies
    /// stay in sync). Clones an existing playlist's header structure (view-settings mhods) so
    /// the result is byte-shaped exactly like the device's own playlists. Returns its new pid.
    /// </summary>
    public ulong CreatePlaylist(string name)
    {
        ulong pid = RandomNonZeroPid();
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
        {
            var template = ds.Playlists!.FirstOrDefault(p => !IsMaster(p));
            byte[] prefix = template is not null ? (byte[])template.Prefix.Clone() : BuildMinimalMhypPrefix();
            if (prefix.Length > 0x14) prefix[0x14] = 0;             // not the master playlist
            if (prefix.Length >= 0x2C) { prefix[0x2A] = 0; prefix[0x2B] = 0; } // not a podcast playlist (clear the flag the template may carry)
            if (prefix.Length >= 0x30) PatchU32(prefix, 0x2C, 0);  // default sort order (don't inherit the template's)
            if (prefix.Length >= 0x24) PatchU64(prefix, 0x1C, pid); // same persistent id in every dataset
            prefix = RebuildPrefixWithName(prefix, name);
            ds.Playlists!.Add(new RawPlaylist { Prefix = prefix, Mhips = new List<byte[]>(), Trailing = Array.Empty<byte>() });
        }
        return pid;
    }

    /// <summary>Append tracks to a playlist (every dataset copy), skipping ones already present. Tracks must already exist in the library.</summary>
    public bool AddTracksToPlaylist(ulong persistentId, IEnumerable<uint> trackIds)
    {
        if (persistentId == 0) return false;
        var ids = trackIds.ToList();
        byte[]? template = FindMhipTemplate();
        bool changed = false;
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!.Where(p => !IsMaster(p) && PlaylistPid(p) == persistentId))
            {
                var present = new HashSet<uint>(pl.Mhips.Select(m => ReadU32(m, 0x18)));
                foreach (uint id in ids)
                    if (present.Add(id))
                    {
                        pl.Mhips.Add(template is not null ? CloneMhip(template, id) : BuildMinimalMhip(id));
                        changed = true;
                    }
            }
        return changed;
    }

    private byte[]? FindMhipTemplate()
    {
        foreach (var ds in Datasets.Where(d => d.Type is 2 or 3 && d.Playlists is not null))
            foreach (var pl in ds.Playlists!)
                if (pl.Mhips.Count > 0) return pl.Mhips[0];
        return null;
    }

    private static byte[] BuildMinimalMhypPrefix()
    {
        const int hl = 0x6C;
        var b = new byte[hl];
        WriteTag(b, ChunkTag.Mhyp);
        PatchU32(b, 0x04, hl); // header length (mhod count / pid / totals filled by caller + serialize)
        return b;
    }

    private static ulong RandomNonZeroPid()
    {
        Span<byte> b = stackalloc byte[8];
        ulong v;
        do { Random.Shared.NextBytes(b); v = BitConverter.ToUInt64(b); } while (v == 0);
        return v;
    }

    /// <summary>
    /// Rebuilds an mhyp prefix, replacing the type-1 (name) mhod and preserving the others
    /// (type 100/102, …). If the prefix is malformed it is returned UNCHANGED rather than
    /// partially rebuilt — a half-rebuilt prefix with a stale mhod count would mis-offset the
    /// member list and corrupt the playlist.
    /// </summary>
    private static byte[] RebuildPrefixWithName(byte[] prefix, string newName)
    {
        int headerLen = (int)ReadU32(prefix, 0x04);
        if (headerLen < 16 || headerLen > prefix.Length) return prefix;
        uint mhodCount = ReadU32(prefix, 0x0C);
        var header = Slice(prefix, 0, headerLen);
        var bodies = new List<byte[]>();
        bool replaced = false;

        int p = headerLen;
        for (uint i = 0; i < mhodCount; i++)
        {
            if (p + 16 > prefix.Length) return prefix;                        // truncated → bail, change nothing
            uint total = ReadU32(prefix, p + 0x08);
            if (total < 12 || p + (long)total > prefix.Length) return prefix;  // malformed → bail
            uint type = ReadU32(prefix, p + 0x0C);
            if (type == 1) { bodies.Add(BuildStringMhod(1, newName)); replaced = true; }
            else bodies.Add(Slice(prefix, p, (int)total));
            p += (int)total;
        }
        if (p < prefix.Length) bodies.Add(Slice(prefix, p, prefix.Length - p)); // preserve any trailing slack
        if (!replaced) { bodies.Insert(0, BuildStringMhod(1, newName)); PatchU32(header, 0x0C, mhodCount + 1); }

        var all = new List<byte[]> { header };
        all.AddRange(bodies);
        return Concat(all);
    }

    private static bool IsMaster(RawPlaylist pl) => pl.Prefix.Length > 0x14 && pl.Prefix[0x14] != 0;

    private static byte[] CloneMhip(byte[] template, uint trackId)
    {
        var m = (byte[])template.Clone();
        PatchU32(m, 0x18, trackId); // mhip.trackId
        return m;
    }

    private static byte[] BuildMinimalMhip(uint trackId)
    {
        const int hl = 0x4C;
        var b = new byte[hl];
        WriteTag(b, ChunkTag.Mhip);
        PatchU32(b, 0x04, hl);      // header length
        PatchU32(b, 0x08, hl);      // total length (no child mhod)
        PatchU32(b, 0x0C, 0);       // mhod child count
        PatchU32(b, 0x18, trackId);
        return b;
    }

    // ---- new-track synthesis ----

    /// <summary>
    /// Build a fresh mhit chunk (standard 0x184 header + string mhods). The Mini's firmware
    /// reads each chunk by its own header length, so a standard-sized header is accepted
    /// alongside the device's larger existing ones. All unset fields are zero, matching how
    /// libgpod/gtkpod author new tracks.
    /// </summary>
    public static byte[] BuildMhitChunk(NewTrack t)
    {
        const int headerLen = 0x184;
        var h = new byte[headerLen];
        WriteTag(h, ChunkTag.Mhit);
        PatchU32(h, 0x04, headerLen);
        PatchU32(h, 0x10, t.UniqueId);
        PatchU32(h, 0x14, 1);                 // visible
        h[0x1D] = t.Type2;                    // MP3 = 1
        uint nowMac = MacTime.FromDateTime(t.DateAdded ?? DateTime.UtcNow);
        PatchU32(h, 0x20, nowMac);            // last modified
        PatchU32(h, 0x24, t.FileSize);
        PatchU32(h, 0x28, t.LengthMs);
        PatchU32(h, 0x2C, t.TrackNumber);
        PatchU32(h, 0x30, t.TotalTracks);
        PatchU32(h, 0x34, t.Year);
        PatchU32(h, 0x38, t.Bitrate);
        PatchU32(h, 0x3C, t.SampleRate << 16); // Hz in the high 16 bits
        PatchU32(h, 0x68, nowMac);            // date added
        PatchU64(h, 0x70, t.Dbid);
        if (t.HasArtwork)                     // cover-art link (offsets from libgpod's mhit; header is 0x184)
        {
            PatchU16(h, 0x7C, 1);             // artwork_count
            PatchU32(h, 0x80, t.ArtworkSize); // artwork_size (sum of thumbnail bytes)
            h[0xA4] = 1;                      // has_artwork: 1 = yes (2 = no)
            PatchU32(h, 0x160, t.MhiiLink);   // mhii_link → ArtworkDB image id
        }
        PatchU32(h, 0xD0, t.MediaType);       // 1 = audio, 2 = movie, 0x20 = music video, 0x40 = TV show
        if (MediaType.IsVideo(t.MediaType))
        {
            // Flags the 5G/Classic firmware checks for video (offsets from libgpod's mk_mhit):
            h[0xA5] = 1;  // skip_when_shuffling — keep videos out of audio shuffle
            h[0xA6] = 1;  // remember_playback_position — resume where you left off
            h[0xB1] = 1;  // movie_flag — "this is a movie, not audio"
        }

        var mhods = new List<byte[]>();
        void Add(uint type, string? s) { if (!string.IsNullOrEmpty(s)) mhods.Add(BuildStringMhod(type, s!)); }
        Add(1, t.Title);
        Add(4, t.Artist);
        Add(3, t.Album);
        Add(5, t.Genre);
        Add(6, t.FileTypeDescription);
        mhods.Add(BuildStringMhod(2, t.Location)); // location is mandatory
        PatchU32(h, 0x0C, (uint)mhods.Count);

        byte[] body = Concat(mhods);
        PatchU32(h, 0x08, (uint)(headerLen + body.Length));
        return Concat(h, body);
    }

    private static byte[] BuildStringMhod(uint type, string value)
    {
        byte[] s = Encoding.Unicode.GetBytes(value);
        var b = new byte[0x28 + s.Length];
        WriteTag(b, ChunkTag.Mhod);
        PatchU32(b, 0x04, 0x18);            // header length
        PatchU32(b, 0x08, (uint)b.Length);  // total length
        PatchU32(b, 0x0C, type);
        PatchU32(b, 0x18, 1);                // position / encoding marker
        PatchU32(b, 0x1C, (uint)s.Length);   // string byte length (code units * 2)
        PatchU32(b, 0x20, 1);
        s.CopyTo(b, 0x28);
        return b;
    }

    // ---- serialize (bottom-up, recomputing only length/count fields) ----

    public byte[] Serialize()
    {
        var dsBytes = Datasets.Select(SerializeDataset).ToList();
        byte[] mhbd = (byte[])MhbdHeader.Clone();
        int total = mhbd.Length + dsBytes.Sum(b => b.Length) + Trailing.Length;
        PatchU32(mhbd, 0x08, (uint)total);          // mhbd total length = whole file
        PatchU32(mhbd, 0x14, (uint)Datasets.Count); // dataset count
        var parts = new List<byte[]> { mhbd };
        parts.AddRange(dsBytes);
        parts.Add(Trailing);
        return Concat(parts);
    }

    private static byte[] SerializeDataset(RawDataset ds)
    {
        if (ds.Verbatim is not null) return ds.Verbatim;

        byte[] children = ds.Tracks is not null
            ? Concat(ds.Tracks)
            : Concat(ds.Playlists!.Select(SerializePlaylist));
        uint childCount = ds.Tracks is not null ? (uint)ds.Tracks.Count : (uint)ds.Playlists!.Count;

        byte[] list = (byte[])ds.ListHeader!.Clone();
        PatchU32(list, 0x08, childCount);           // mhlt/mhlp off8 = CHILD COUNT

        byte[] mhsd = (byte[])ds.MhsdHeader!.Clone();
        int total = mhsd.Length + list.Length + children.Length + ds.Trailing.Length;
        PatchU32(mhsd, 0x08, (uint)total);          // mhsd total length

        return Concat(mhsd, list, children, ds.Trailing);
    }

    private static byte[] SerializePlaylist(RawPlaylist pl)
    {
        byte[] mhips = Concat(pl.Mhips);
        byte[] prefix = (byte[])pl.Prefix.Clone();
        int total = prefix.Length + mhips.Length + pl.Trailing.Length;
        PatchU32(prefix, 0x08, (uint)total);        // mhyp total length
        PatchU32(prefix, 0x10, (uint)pl.Mhips.Count); // mhyp mhip item count
        return Concat(prefix, mhips, pl.Trailing);
    }

    // ---- helpers ----

    private static byte[] Slice(byte[] b, int off, int len)
    {
        var r = new byte[len];
        Buffer.BlockCopy(b, off, r, 0, len);
        return r;
    }

    private static void PatchU16(byte[] buf, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), v);
    private static void PatchU32(byte[] buf, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);

    private static void PatchU64(byte[] buf, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off, 8), v);

    public static uint ReadU32(byte[] buf, int off) => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off, 4));

    public static ulong ReadU64(byte[] buf, int off) => BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off, 8));

    private static void WriteTag(byte[] buf, string tag) => Encoding.ASCII.GetBytes(tag, 0, 4, buf, 0);

    private static byte[] Concat(params byte[][] parts) => Concat((IEnumerable<byte[]>)parts);

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var list = parts as IList<byte[]> ?? parts.ToList();
        int total = 0;
        foreach (var p in list) total += p.Length;
        var result = new byte[total];
        int o = 0;
        foreach (var p in list) { Buffer.BlockCopy(p, 0, result, o, p.Length); o += p.Length; }
        return result;
    }
}

/// <summary>One dataset (mhsd). Either kept verbatim, or split into editable track/playlist children.</summary>
internal sealed class RawDataset
{
    public uint Type;
    public byte[]? Verbatim;          // type 4/5/9 etc. — preserved untouched
    public byte[]? MhsdHeader;        // type 1/2/3
    public byte[]? ListHeader;        // mhlt (type 1) or mhlp (type 2/3)
    public List<byte[]>? Tracks;      // type 1: each entry = one full mhit chunk (+ its mhods)
    public List<RawPlaylist>? Playlists; // type 2/3
    public byte[] Trailing = Array.Empty<byte>();
}

/// <summary>One playlist (mhyp): a verbatim header/name prefix + the list of member mhip chunks.</summary>
internal sealed class RawPlaylist
{
    public byte[] Prefix = Array.Empty<byte>(); // mhyp header + name/settings mhods (up to first mhip)
    public List<byte[]> Mhips = new();          // each = one full mhip chunk (+ its child mhod)
    public byte[] Trailing = Array.Empty<byte>();
}

/// <summary>
/// Fields to change on an existing track (see <see cref="RawDb.EditTrack"/>). Each is OPTIONAL:
/// a null string/uint/byte means "leave this field as-is"; a non-null string sets it (an empty
/// string clears/removes that tag). Rating is stars×20 (0/20/40/60/80/100).
/// </summary>
internal sealed class TrackEdit
{
    public string? Title;
    public string? Artist;
    public string? Album;
    public string? AlbumArtist;
    public string? Genre;
    public uint? Year;
    public uint? TrackNumber;
    public byte? Rating;
}

/// <summary>Input spec for synthesizing a new track's mhit (see <see cref="RawDb.BuildMhitChunk"/>).</summary>
internal sealed class NewTrack
{
    public uint UniqueId;
    public ulong Dbid;
    public string? Title;
    public string? Artist;
    public string? Album;
    public string? Genre;
    public string? FileTypeDescription;   // mhod type 6, e.g. "MPEG audio file"
    public string Location = "";          // ":iPod_Control:Music:Fnn:NAME.mp3" — mandatory
    public uint FileSize;
    public uint LengthMs;
    public uint Bitrate;
    public uint SampleRate;               // Hz (written shifted into the high 16 bits)
    public uint Year;
    public uint TrackNumber;
    public uint TotalTracks;
    public byte Type2 = 1;                // MP3 = 1, AAC = 0
    public uint MediaType = 1;            // 1 = audio
    public DateTime? DateAdded;

    // Cover art (set only when the device shows artwork and the source had an embedded image).
    public bool HasArtwork;
    public uint MhiiLink;                 // the ArtworkDB image id (mhii @0x10) this track links to
    public uint ArtworkSize;              // total bytes of this track's thumbnails (mhit @0x80)
}
