using System.Buffers.Binary;

namespace iPodCommander;

/// <summary>
/// Reads the iPod's on-device "Play Counts" file (iPod_Control/iTunes/Play Counts): the play counts and
/// star ratings the iPod records as you listen, which iTunes normally only folds into the iTunesDB when
/// you sync. Since Mixtape users don't sync with iTunes, that data would otherwise stay invisible.
///
/// Entries are positional — one per track, in the iTunesDB's on-disk mhit order — so we align them to
/// tracks via the RAW mhit unique-id list (not the reader's track list), which means a track the reader
/// skipped can't shift every later entry onto the wrong song. Display-only: this never feeds back into a
/// write (<see cref="IpodLibrary.Save"/> serializes the RawDb, not the View).
/// </summary>
internal static class PlayCounts
{
    private readonly record struct Entry(uint PlayCount, byte Rating);

    /// <summary>Overlay on-device play counts + ratings onto the library's display model. Returns #tracks updated.</summary>
    public static int Apply(IpodLibrary lib)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(lib.Device.ITunesDbPath)!, "Play Counts");
            var entries = Read(path);
            if (entries is null || entries.Count == 0) return 0;

            var rawTracks = lib.Raw.Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks;
            // Positional alignment is only safe when the file matches the database track-for-track.
            // If they differ (e.g. tracks were added/removed since the iPod last wrote the file), skip
            // rather than risk attaching a play count to the wrong song.
            if (rawTracks is null || rawTracks.Count != entries.Count) return 0;

            var byId = new Dictionary<uint, Track>();
            foreach (var t in lib.View.Tracks) byId[t.UniqueId] = t;

            int updated = 0;
            for (int i = 0; i < rawTracks.Count; i++)
            {
                byte[] mhit = rawTracks[i];
                if (mhit.Length < 0x14) continue;
                uint uid = BinaryPrimitives.ReadUInt32LittleEndian(mhit.AsSpan(0x10));
                if (!byId.TryGetValue(uid, out var t)) continue;
                var e = entries[i];
                bool any = false;
                if (e.PlayCount > 0) { t.PlayCount += e.PlayCount; any = true; } // on-device plays since last sync → add to the DB total
                if (e.Rating > 0) { t.Rating = e.Rating; any = true; }           // a rating set on the iPod wins
                if (any) updated++;
            }
            return updated;
        }
        catch { return 0; } // a display nicety — never let it block loading the library
    }

    /// <summary>
    /// Fold the on-device "Play Counts" into the RAW database so a save PERSISTS them — the iTunes-style sync
    /// the no-iTunes crowd otherwise never gets. Plays are ADDED to the DB total; a rating set on the iPod
    /// REPLACES the DB one (mirrors <see cref="Apply"/>'s semantics + its positional alignment). Only the two
    /// fields whose offsets are confirmed are written (play count @0x50, rating @0x1F) — never last-played/skip.
    /// Returns the file path to delete AFTER a successful write, plus a pre-fold snapshot of the RawDb so a
    /// FAILED write can roll the fold back without losing the user's other staged edits; null = nothing folded.
    /// </summary>
    public static (string path, byte[] preFold)? FoldIntoRaw(IpodLibrary lib)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(lib.Device.ITunesDbPath)!, "Play Counts");
            var entries = Read(path);
            if (entries is null || entries.Count == 0) return null;
            var rawTracks = lib.Raw.Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks;
            // Same safety as Apply: positional alignment is only valid when the file matches the DB track-for-track.
            if (rawTracks is null || rawTracks.Count != entries.Count) return null;
            bool anyData = false;
            foreach (var e in entries) if (e.PlayCount > 0 || e.Rating > 0) { anyData = true; break; }
            if (!anyData) return null;

            byte[] preFold = lib.Raw.Serialize();   // snapshot BEFORE mutating, for safe rollback on a failed write
            bool folded = false;
            for (int i = 0; i < rawTracks.Count; i++)
            {
                byte[] mhit = rawTracks[i];
                if (mhit.Length <= 0x53) continue;                  // need play count @0x50..0x53
                var e = entries[i];
                if (e.PlayCount == 0 && e.Rating == 0) continue;
                uint uid = BinaryPrimitives.ReadUInt32LittleEndian(mhit.AsSpan(0x10));
                var edit = new TrackEdit();
                if (e.PlayCount > 0) edit.PlayCount = BinaryPrimitives.ReadUInt32LittleEndian(mhit.AsSpan(0x50)) + e.PlayCount; // add on-device plays
                if (e.Rating > 0) edit.Rating = e.Rating;                                                                       // on-device rating wins
                if (lib.Raw.EditTrack(uid, edit)) folded = true;
            }
            return folded ? (path, preFold) : null;
        }
        catch { return null; }   // best-effort: never let a play-counts quirk block a save
    }

    private static List<Entry>? Read(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] b;
        try { b = File.ReadAllBytes(path); } catch { return null; }
        // Header: "mhdp", headerLen(@4), entryLen(@8), entryCount(@0xC).
        if (b.Length < 16 || b[0] != (byte)'m' || b[1] != (byte)'h' || b[2] != (byte)'d' || b[3] != (byte)'p') return null;
        int hdr = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        int elen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        long cnt = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        if (hdr < 16 || elen < 12 || cnt < 0 || cnt > 1_000_000) return null;
        var list = new List<Entry>((int)Math.Min(cnt, 100_000));
        for (long e = 0; e < cnt; e++)
        {
            long o = (long)hdr + e * elen;
            if (o + elen > b.Length) break;
            int oi = (int)o;
            uint play = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(oi));        // +0x00 play count
            byte rating = elen >= 16 ? (byte)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(oi + 12)) : (byte)0; // +0x0C rating (stars×20)
            list.Add(new Entry(play, rating));
        }
        return list;
    }
}
