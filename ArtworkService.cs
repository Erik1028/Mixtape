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
    private static readonly object Gate = new();

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
        string ck = key + "#" + size;
        lock (Gate) { if (Cache.TryGetValue(ck, out var hit)) return hit; }

        Bitmap? result = null;
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                using var f = TagLib.File.Create(filePath);
                var pics = f.Tag.Pictures;
                if (pics.Length > 0 && pics[0].Data.Data.Length > 0)
                {
                    using var ms = new MemoryStream(pics[0].Data.Data);
                    using var src = Image.FromStream(ms);
                    result = RoundScaled(src, size);
                }
            }
        }
        catch { /* unreadable art → treat as none */ }

        lock (Gate) Cache[ck] = result;
        return result;
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
