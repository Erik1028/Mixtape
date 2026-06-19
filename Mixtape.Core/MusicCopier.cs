namespace iPodCommander;

/// <summary>
/// Copies an audio file into the iPod's Music buckets (F00..F(n-1)) and returns the iPod-
/// relative location string the database stores (':'-separated, leading colon). The bucket
/// is cosmetic load-balancing; the location string is the authoritative pointer.
/// </summary>
internal static class MusicCopier
{
    /// <summary>Copies <paramref name="sourcePath"/> onto the device; returns (location, destPath).</summary>
    public static (string Location, string DestPath) Copy(IPodDevice device, string sourcePath)
    {
        int buckets = Math.Clamp(device.Profile.MusicDirCount, 1, 100); // iPods use 2-digit Fnn buckets
        string musicDir = device.MusicDir;
        Directory.CreateDirectory(musicDir);

        string bucket = ChooseLeastFullBucket(musicDir, buckets);
        string bucketDir = Path.Combine(musicDir, bucket);
        Directory.CreateDirectory(bucketDir);

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant(); // some iPods dislike uppercase ext

        // Claim a free name ATOMICALLY (FileMode.CreateNew) rather than check-then-copy, so two
        // concurrent adds (or a repeated random draw) can never collide and throw mid-copy.
        for (int attempt = 0; attempt < 1_000_000; attempt++)
        {
            string name = $"ipcm{Random.Shared.Next(0, 1_000_000):D6}{ext}";
            string dest = Path.Combine(bucketDir, name);
            try { using (new FileStream(dest, FileMode.CreateNew, FileAccess.Write)) { } }
            catch (IOException) { continue; } // name already taken — draw another
            // If the copy fails (disk full, source vanished, USB drop), delete the 0-byte/partial file we just
            // claimed so it doesn't linger on the device as an orphan, then rethrow for the caller to report.
            try { File.Copy(sourcePath, dest, overwrite: true); }
            catch { try { File.Delete(dest); } catch { } throw; }
            // ":iPod_Control:Music:F03:ipcm012345.mp3" — well under the ~112-byte device limit.
            return ($":iPod_Control:Music:{bucket}:{name}", dest);
        }
        throw new IOException($"No free filename in {bucketDir}");
    }

    private static string ChooseLeastFullBucket(string musicDir, int buckets)
    {
        string best = "F00";
        int bestCount = int.MaxValue;
        for (int i = 0; i < buckets; i++)
        {
            string f = $"F{i:00}";
            string d = Path.Combine(musicDir, f);
            int count = Directory.Exists(d) ? Directory.GetFiles(d).Length : 0;
            if (count < bestCount) { bestCount = count; best = f; }
        }
        return best;
    }

}
