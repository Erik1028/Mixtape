namespace iPodCommander;

/// <summary>
/// Writes new iTunesDB bytes to the device as safely as a FAT32 volume allows:
/// back up the current DB, write a temp file, swap it in, then re-read and verify the result.
/// If the written file doesn't parse or the track count is wrong, the backup is restored and
/// the operation throws — the device is never left with a DB we couldn't read back.
/// </summary>
internal static class SafeDbWriter
{
    public static void Write(IPodDevice device, byte[] bytes, int expectedTrackCount)
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
        }
        catch
        {
            if (File.Exists(bak)) File.Copy(bak, db, overwrite: true);
            throw;
        }
    }
}
