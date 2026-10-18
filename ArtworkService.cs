using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Extracts embedded cover art from the audio files on the iPod (via TagLib#), scaled to a
/// rounded square and cached by album so each album's file is read once. Returns null when a
/// file has no embedded picture (callers fall back to a generated gradient tile). Safe to call
/// from a background thread; the cache is guarded by a lock.
/// </summary>
internal static class ArtworkService
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();
    private static readonly object Gate = new();          // guards the cache dictionary
    private static readonly object DecodeGate = new();    // serializes GDI+ image decode (System.Drawing is NOT thread-safe)
    private const int CacheCap = 384;                     // bound the cache so it can't grow unbounded across a long session
    private static readonly Queue<string> Order = new();  // insertion order for FIFO eviction (guarded by Gate)

    // Insert under the Gate lock and FIFO-trim to CacheCap. We deliberately do NOT Dispose evicted bitmaps: a caller
    // (or the cover-flow baker thread) may still hold/draw a reference we returned earlier, so eviction only drops OUR
    // reference and lets the GC reclaim it once unreferenced — that bounds growth with zero use-after-dispose risk.
    /// <summary>Diagnostics: how many bitmaps the cache holds and their pixel bytes (32 bpp).</summary>
    public static (int Count, long Bytes) CacheStats()
    {
        lock (Gate) { long b = 0; int n = 0; foreach (var v in Cache.Values) if (v is not null) { n++; b += (long)v.Width * v.Height * 4; } return (n, b); }
    }

    private static void RememberLocked(string key, Bitmap? value)
    {
        Cache[key] = value;
        Order.Enqueue(key);
        while (Cache.Count > CacheCap && Order.Count > 0)
        {
            string old = Order.Dequeue();
            if (old != key) Cache.Remove(old);   // never evict the entry we just added
        }
    }

    public static string KeyFor(Track t) =>
        !string.IsNullOrEmpty(t.Album) ? "alb:" + t.Album!.ToLowerInvariant()
        : !string.IsNullOrEmpty(t.LocalPath) ? "loc:" + t.LocalPath!.ToLowerInvariant()   // unique per PC file (else albumless locals collide)
        : "loc:" + (t.Location ?? "");

    /// <summary>Cached embedded-art bitmap for the key at the given size, or null if none / not loaded yet.</summary>
    public static Bitmap? TryGet(string key, int size)
    {
        lock (Gate) return Cache.TryGetValue(key + "#" + size, out var b) ? b : null;
    }

    /// <summary>Loads (and caches) the embedded art for a file. Reads the file only once per key+size; caches null too.</summary>
    public static Bitmap? Load(string key, string? filePath, int size)
    {
        string ck = key + "#" + size;                          // album key: shared so an album is read once
        string nk = "noart:" + (filePath ?? "") + "#" + size;  // per-FILE "no art" memo — never poisons the album key
        lock (Gate)
        {
            if (Cache.TryGetValue(ck, out var hit) && hit is not null) return hit;   // album cover already loaded
            if (Cache.ContainsKey(nk) && CoverDownloads.PathFor(key) is null) return null;   // this exact file already known artless (and nothing downloaded since)
        }

        // Serialize the actual decode: concurrent System.Drawing decodes (e.g. a batch import loading many covers
        // on background threads) throw "a generic error occurred in GDI+". One decode at a time avoids that.
        lock (DecodeGate)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(ck, out var hit2) && hit2 is not null) return hit2;   // filled while we waited
                if (Cache.ContainsKey(nk) && CoverDownloads.PathFor(key) is null) return null;
            }
            try
            {
                Bitmap? result = null;
                // A cover read before is on disk at this size (Kept): a launch no longer opens every song's file and
                // decodes its full picture again.
                string? kept = Kept.PathFor(filePath, size);
                if (kept is not null && Kept.Read(kept) is { } square)
                {
                    using (square) result = RoundScaled(square, size);
                }
                // One extractor for both display + iPod sync: embedded front cover, else an external cover image —
                // and failing both, a cover downloaded for the album (CoverDownloads), served the same way.
                else if (ArtBytes(key, filePath, out bool fromFile) is { Length: > 0 } bytes)
                {
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    if (fromFile && kept is not null)
                    {
                        // (only a file's own picture is kept under its name: a downloaded cover can still be swapped)
                        using var sq = SquareScaled(src, size);
                        Kept.Write(kept, sq);
                        result = RoundScaled(sq, size);
                    }
                    else result = RoundScaled(src, size);
                }
                // Cache a SUCCESS under the shared album key (so the whole album reuses it); cache a MISS only
                // under the per-file memo — so a track with no art can't deny another track (or another source,
                // e.g. an iPod track with no on-disk file) that DOES have a cover for the same album.
                lock (Gate) RememberLocked(result is not null ? ck : nk, result);
                return result;
            }
            catch { return null; }   // transient decode failure → do NOT cache, so a later call can retry
        }
    }

    /// <summary>Like <see cref="Load"/> but returns a SHARP, full-bleed square (NO rounded corners / edge) at
    /// high resolution — for callers that clip and round it themselves (e.g. the mini-player cover hero, which
    /// otherwise upscales a tiny grid thumbnail and looks blurry). Cached separately from the rounded variant.</summary>
    public static Bitmap? LoadSquare(string key, string? filePath, int size)
    {
        string ck = key + "#sq#" + size;
        string nk = "noartsq:" + (filePath ?? "") + "#" + size;
        lock (Gate)
        {
            if (Cache.TryGetValue(ck, out var hit) && hit is not null) return hit;
            if (Cache.ContainsKey(nk)) return null;
        }

        lock (DecodeGate)   // serialize the GDI+ decode (see Load) so concurrent loads don't fail
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(ck, out var hit2) && hit2 is not null) return hit2;
                if (Cache.ContainsKey(nk) && CoverDownloads.PathFor(key) is null) return null;
            }
            try
            {
                Bitmap? result = null;
                if (ArtBytes(key, filePath) is { Length: > 0 } bytes)
                {
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    var bmp = new Bitmap(size, size);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float scale = Math.Max((float)size / src.Width, (float)size / src.Height);   // cover-fit / center-crop
                        float w = src.Width * scale, h = src.Height * scale;
                        g.DrawImage(src, (size - w) / 2f, (size - h) / 2f, w, h);
                    }
                    result = bmp;
                }
                lock (Gate) RememberLocked(result is not null ? ck : nk, result);
                return result;
            }
            catch { return null; }   // transient decode failure → don't cache
        }
    }

    /// <summary>The cover bytes for a track: its file's embedded / folder art, else the album's downloaded cover.</summary>
    private static byte[]? ArtBytes(string key, string? filePath) => ArtBytes(key, filePath, out _);

    private static byte[]? ArtBytes(string key, string? filePath, out bool fromFile)
    {
        byte[]? bytes = null;
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) bytes = MetadataExtractor.ReadArt(filePath);
        fromFile = bytes is { Length: > 0 };
        return fromFile ? bytes : CoverDownloads.ReadBytes(key);
    }

    /// <summary>The picture cover-fit into a size×size square - what <see cref="RoundScaled"/> draws inside its corners.</summary>
    private static Bitmap SquareScaled(Image src, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Max((float)size / src.Width, (float)size / src.Height);
        float w = src.Width * scale, h = src.Height * scale;
        g.DrawImage(src, (size - w) / 2f, (size - h) / 2f, w, h);
        return bmp;
    }

    /// <summary>The self-test: a tile made from the kept square against one made from the full picture - how many pixels
    /// differ, and by how much at most (a channel's levels).</summary>
    internal static (int Diff, int Worst, int Inner) KeptCoverSelfTest()
    {
        using var src = new Bitmap(1200, 900);
        using (var g = Graphics.FromImage(src))
        {
            using var lg = new LinearGradientBrush(new Rectangle(0, 0, 1200, 900), Color.FromArgb(255, 200, 30, 60), Color.FromArgb(255, 20, 120, 240), 35f);
            g.FillRectangle(lg, 0, 0, 1200, 900);
            for (int i = 0; i < 40; i++) using (var p = new Pen(Color.FromArgb(255, (i * 37) % 256, (i * 91) % 256, (i * 53) % 256), 3)) g.DrawEllipse(p, 30 * i, 20 * i % 800, 140, 90);
        }
        int diff = 0, worst = 0, inner = 0;
        foreach (int size in new[] { 150, 36 })
        {
            using var direct = RoundScaled(src, size);
            using var sq = SquareScaled(src, size);
            string tmp = Path.Combine(Path.GetTempPath(), "mixtape-kept-selftest.png");
            sq.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            using var back = Kept.Read(tmp)!;
            File.Delete(tmp);
            using var viaKept = RoundScaled(back, size);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                Color a = direct.GetPixel(x, y), b = viaKept.GetPixel(x, y);
                int d = Math.Max(Math.Max(Math.Abs(a.A - b.A), Math.Abs(a.R - b.R)), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                if (d > 0) { diff++; worst = Math.Max(worst, d); if (a.A == 255 && b.A == 255) inner++; }
            }
        }
        return (diff, worst, inner);
    }

    /// <summary>
    /// Covers as they were last decoded, kept on disk (%LOCALAPPDATA%\Mixtape\thumbs) at the size they are shown. A
    /// launch used to open every visible song's file and decode its full picture again (often 1000+ px, ~10 ms each) -
    /// the Home page's covers streaming in for a quarter of a second (MIX_STARTUP). The square is kept, not the finished
    /// tile, so the look's corners are still drawn at run time. Keyed by the file's path, length and time: a changed file
    /// is read again. Only this PC's disk is written - never the iPod's.
    /// </summary>
    private static class Kept
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mixtape", "thumbs");
        private const int MaxFiles = 3000;   // ~40 KB each at 150 px: a bound of ~120 MB, pruned oldest-first
        private static int _pruned;

        public static string? PathFor(string? file, int size)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) return null;
                string id = fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + size;
                return Path.Combine(Dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(id))) + ".png");
            }
            catch { return null; }
        }

        public static Bitmap? Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms);
                return new Bitmap(img);   // its own pixels: the stream is closed here
            }
            catch { return null; }
        }

        public static void Write(string path, Bitmap square)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
                square.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                File.Move(tmp, path, overwrite: true);
                if (Interlocked.Exchange(ref _pruned, 1) == 0) Task.Run(Prune);   // once a session, off the decode
            }
            catch { }
        }

        private static void Prune()
        {
            try
            {
                var files = new DirectoryInfo(Dir).GetFiles("*.png");
                if (files.Length <= MaxFiles) return;
                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxFiles * 4 / 5)) { try { f.Delete(); } catch { } }
            }
            catch { }
        }
    }

    private static Bitmap RoundScaled(Image src, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // a kept square is already this size: copied as it is (a bicubic pass at 1:1 would soften it a hair)
        g.InterpolationMode = src.Width == size && src.Height == size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var path = Theme.RoundedRect(new RectangleF(0, 0, size - 1, size - 1), Math.Max(2, size * Theme.TileFrac)))
            g.SetClip(path);
        float scale = Math.Max((float)size / src.Width, (float)size / src.Height); // cover-fit / center-crop
        float w = src.Width * scale, h = src.Height * scale;
        g.DrawImage(src, (size - w) / 2f, (size - h) / 2f, w, h);
        g.ResetClip();
        using (var ip = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), Math.Max(2, size * Theme.TileFrac)))
        using (var pen = new Pen(Color.FromArgb(36, 255, 255, 255)))
            g.DrawPath(pen, ip);
        return bmp;
    }
}
