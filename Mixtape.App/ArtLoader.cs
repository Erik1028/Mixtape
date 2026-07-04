using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using iPodCommander;

namespace Mixtape.App;

/// <summary>Loads + caches embedded album-art thumbnails (via TagLib# in Mixtape.Core), off the UI thread.</summary>
internal static class ArtLoader
{
    private static readonly Dictionary<string, Bitmap?> _cache = new();
    private static readonly Queue<string> _order = new();   // FIFO insertion order for the cap
    private const int Cap = 384;                             // bound the cache (mirrors the WinForms ArtworkService/Theme caches)
    private static readonly SemaphoreSlim _gate = new(3);   // limit concurrent decodes

    public static async Task<Bitmap?> LoadAsync(string path, string key)
    {
        lock (_cache) { if (_cache.TryGetValue(key, out var hit)) return hit; }

        await _gate.WaitAsync();
        try
        {
            var bmp = await Task.Run(() =>
            {
                try
                {
                    var bytes = MetadataExtractor.ReadArt(path);
                    if (bytes is null) return (Bitmap?)null;
                    using var ms = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(ms, 96);   // scaled-down thumbnail decode
                }
                catch { return null; }
            });
            lock (_cache)
            {
                if (!_cache.ContainsKey(key)) _order.Enqueue(key);
                _cache[key] = bmp;
                // Evict oldest beyond the cap. Do NOT Dispose the evicted bitmap — a visible TrackRow.Art may still
                // reference it (disposing would render a disposed image); dropping the dict ref lets GC reclaim it
                // once no row holds it. Same evict-without-dispose caution as the WinForms caches.
                while (_order.Count > Cap)
                {
                    var old = _order.Dequeue();
                    if (!ReferenceEquals(old, key)) _cache.Remove(old);
                }
            }
            return bmp;
        }
        finally { _gate.Release(); }
    }
}
