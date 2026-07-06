namespace iPodCommander;

/// <summary>
/// Writes new iTunesDB bytes to the device as safely as a FAT32 volume allows:
/// back up the current DB, write a temp file, swap it in, then re-read and verify the result.
/// If the written file doesn't parse or the track count is wrong, the backup is restored and
/// the operation throws — the device is never left with a DB we couldn't read back.
/// </summary>
internal static class SafeDbWriter
{
    /// <param name="maxWarnings">The pre-write library's structural-warning count. The read-back must not EXCEED it
    /// (a NEW warning means the write malformed something) — passing the baseline instead of 0 avoids false-rejecting
    /// a DB that already had a benign warning before we touched it.</param>
    /// <param name="expectedMasterCount">The pre-write master-playlist count. Checked RELATIVE to the baseline, not
    /// against an absolute 1 — some real DBs carry 2 IsMaster lists, so the invariant is "a write must not drop or
    /// duplicate the master(s)", never "there is exactly one".</param>
    public static void Write(IPodDevice device, byte[] bytes, int expectedTrackCount, int maxWarnings, int expectedMasterCount)
    {
        string db = device.ITunesDbPath;
        string bak = db + ".bak";
        string tmp = db + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(db)!);

        // 0) One-time pristine snapshot: the very first database we ever write over is kept
        //    forever as iTunesDB.original. The rolling .bak (step 1) can be overwritten by a
        //    later write, but this never is — so the user can always get back to square one.
        string original = db + ".original";
        if (File.Exists(db) && !File.Exists(original)) File.Copy(db, original);

        // 1) Back up the current database (rolling backup, previous-state).
        if (File.Exists(db)) File.Copy(db, bak, overwrite: true);

        // 2) Write to a temp file in the same folder and flush to disk.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        // 3) Swap it in. File.Replace is best-effort atomic; FAT32 may reject it, so fall back.
        try
        {
            File.Replace(tmp, db, null);
        }
        catch
        {
            File.Copy(tmp, db, overwrite: true);
            try { File.Delete(tmp); } catch { /* leftover temp is harmless */ }
        }

        // 4) Verify by reading the just-written DB back; roll back on any problem.
        try
        {
            var check = ITunesDbReader.ReadFile(db);
            if (check.Tracks.Count != expectedTrackCount)
                throw new InvalidDataException($"Verify failed: wrote {check.Tracks.Count} tracks, expected {expectedTrackCount}.");
            // Structure sanity, not just the count: a malformed playlist/mhip can leave the track count right yet the
            // library broken — the reader is TOLERANT (it records a Warning and reads on rather than throwing), so the
            // count alone can't catch it. Both checks are RELATIVE to the pre-write baseline, so an unusual-but-valid
            // DB (e.g. one that already carries 2 masters, or a benign warning) is never false-rejected.
            int masters = 0; foreach (var p in check.Playlists) if (p.IsMaster) masters++;
            if (masters != expectedMasterCount)
                throw new InvalidDataException($"Verify failed: the written DB has {masters} master playlists (expected {expectedMasterCount}) — the master was dropped or duplicated.");
            if (check.Warnings.Count > maxWarnings)
                throw new InvalidDataException($"Verify failed: {check.Warnings.Count} structural warning(s) after write (was {maxWarnings}) — the DB may be malformed.");
        }
        catch (Exception verifyEx)
        {
            // Roll back to the known-good backup. If the RESTORE itself fails, don't let the original error
            // mask a now-corrupt device — surface an actionable message telling the user where the good backup is.
            if (File.Exists(bak))
            {
                try { File.Copy(bak, db, overwrite: true); }
                catch (Exception restoreEx)
                {
                    throw new IOException(
                        "The iTunesDB write failed verification AND the automatic restore also failed. Your library " +
                        $"database may be corrupt. A known-good backup is at \"{bak}\" — copy it over \"{db}\" to recover. " +
                        $"Restore error: {restoreEx.Message}", verifyEx);
                }
            }
            throw;
        }
    }
}
