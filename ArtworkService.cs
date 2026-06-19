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
            if (Cache.ContainsKey(nk)) return null;                                  // this exact file already known artless
        }

        // Serialize the actual decode: concurrent System.Drawing decodes (e.g. a batch import loading many covers
        // on background threads) throw "a generic error occurred in GDI+". One decode at a time avoids that.
        lock (DecodeGate)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(ck, out var hit2) && hit2 is not null) return hit2;   // filled while we waited
                if (Cache.ContainsKey(nk)) return null;
            }
            try
            {
                Bitmap? result = null;
                // One extractor for both display + iPod sync: embedded front cover, else an external cover image.
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath) && MetadataExtractor.ReadArt(filePath) is { Length: > 0 } bytes)
                {
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    result = RoundScaled(src, size);
                }
                // Cache a SUCCESS under the shared album key (so the whole album reuses it); cache a MISS only
                // under the per-file memo — so a track with no art can't deny another track (or another source,
                // e.g. an iPod track with no on-disk file) that DOES have a cover for the same album.
                lock (Gate) { if (result is not null) Cache[ck] = result; else Cache[nk] = null; }
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
                if (Cache.ContainsKey(nk)) return null;
            }
            try
            {
                Bitmap? result = null;
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath) && MetadataExtractor.ReadArt(filePath) is { Length: > 0 } bytes)
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
                lock (Gate) { if (result is not null) Cache[ck] = result; else Cache[nk] = null; }
                return result;
            }
            catch { return null; }   // transient decode failure → don't cache
        }
    }

    private static Bitmap RoundScaled(Image src, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
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
