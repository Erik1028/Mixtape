using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Covers for albums whose files carry none, from the internet — opt-out in Settings. The lookup is
/// <see cref="CoverArtLookup"/> (artist + album against Apple's public music search, then MusicBrainz + the Cover
/// Art Archive; a candidate is used only when it clearly matches). This class fetches the image once, keeps it in
/// %APPDATA%\Mixtape\covers\, and <see cref="ArtworkService"/> serves it under the album's art key exactly like an
/// embedded cover — so the album grid, the header, the deck card, the list rows, Cover Flow and the mini player all
/// receive it through the one path they already use. One background worker, sequential (the services ask for
/// politeness), a short back-off when the network is down. Optionally the image is also written into a LOCAL
/// file's own tag, so other players see it; files on the iPod are never touched.
/// </summary>
internal static class CoverDownloads
{
    public static bool Enabled;       // Settings → "Missing covers from the internet"
    public static bool EmbedLocal;    // Settings → "Write covers into local files"
    /// <summary>UI thread: a cover landed for this base art key ("alb:…") — refresh whatever shows the album.</summary>
    public static event Action<string>? Arrived;

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixtape", "covers");
    private static readonly object Gate = new();
    private static readonly HashSet<string> _have = new(StringComparer.Ordinal);     // base keys with an image on disk
    private static readonly HashSet<string> _queued = new(StringComparer.Ordinal);   // in the queue or in flight
    private static readonly Queue<(string Key, Track Track, string? Path)> _work = new();
    private static readonly AutoResetEvent _signal = new(false);
    private static Thread? _worker;
    private static Control? _ui;
    private static bool _scanned;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 6 * 1024 * 1024 };

    /// <summary>Call once from the main form (marshals <see cref="Arrived"/> onto its thread).</summary>
    public static void Init(Control ui)
    {
        _ui = ui;
        try { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mixtape/1.0 (+https://github.com/fgs8z2n9qh-tech/Mixtape)"); } catch { }
        Scan();
    }

    private static void Scan()
    {
        lock (Gate)
        {
            if (_scanned) return;
            _scanned = true;
            try { if (Directory.Exists(Dir)) foreach (var f in Directory.EnumerateFiles(Dir, "*.img")) _have.Add(Path.GetFileNameWithoutExtension(f)); }
            catch { }
        }
    }

    /// <summary>"br:alb:x" / "cf:alb:x" / "alb:x" → "alb:x"; null for keys that are not an album ("loc:…").</summary>
    public static string? BaseKey(string? artKey)
    {
        if (string.IsNullOrEmpty(artKey)) return null;
        int i = artKey.IndexOf("alb:", StringComparison.Ordinal);
        return i < 0 ? null : artKey.Substring(i);
    }

    private static string FileNameFor(string baseKey)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(baseKey));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    /// <summary>The downloaded image for this art key, or null. Cheap (no I/O once scanned).</summary>
    public static string? PathFor(string? artKey)
    {
        var b = BaseKey(artKey);
        if (b is null) return null;
        Scan();
        string name = FileNameFor(b);
        lock (Gate) if (!_have.Contains(name)) return null;
        return Path.Combine(Dir, name + ".img");
    }

    public static byte[]? ReadBytes(string? artKey)
    {
        var p = PathFor(artKey);
        if (p is null) return null;
        try { return File.ReadAllBytes(p); } catch { return null; }
    }

    /// <summary>A loader found no cover for this track: ask for one (deduplicated per album; a no-op while off,
    /// for albumless tracks, for videos, and for albums already answered).</summary>
    public static void Request(Track t, string? filePath)
    {
        if (!Enabled || t is null || string.IsNullOrWhiteSpace(t.Album) || !MediaType.IsAudio(t.MediaType)) return;
        var b = BaseKey(ArtworkService.KeyFor(t));
        if (b is null) return;
        Scan();
        string name = FileNameFor(b);
        lock (Gate)
        {
            if (_have.Contains(name) || !_queued.Add(b)) return;
            _work.Enqueue((b, t, filePath));
            if (_worker is null) { _worker = new Thread(Loop) { IsBackground = true, Name = "CoverDownloads", Priority = ThreadPriority.BelowNormal }; _worker.Start(); }
        }
        _signal.Set();
    }

    private static void Loop()
    {
        int failures = 0;
        while (true)
        {
            (string Key, Track Track, string? Path) job;
            lock (Gate) { if (_work.Count == 0) job = default; else job = _work.Dequeue(); }
            if (job.Key is null) { _signal.WaitOne(1000); continue; }
            bool ok = false;
            try
            {
                if (Enabled) ok = Fetch(job.Key, job.Track, job.Path);
                failures = ok ? 0 : failures;
            }
            catch { failures++; }
            finally { lock (Gate) _queued.Remove(job.Key); }
            if (failures >= 3) { failures = 0; Thread.Sleep(60_000); }   // offline / proxy trouble: back off, don't hammer
        }
    }

    /// <summary>Look the album up, fetch the image, keep it. True = a cover landed.</summary>
    private static bool Fetch(string baseKey, Track t, string? filePath)
    {
        string artist = t.AlbumArtist ?? t.Artist ?? "";
        string? url = null;
        try { url = CoverArtLookup.Find(artist, t.Album, null); }   // cached answers (hits and expiring misses) come back instantly
        catch { throw; }
        if (string.IsNullOrEmpty(url)) return false;
        byte[] bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (bytes.Length < 1024) return false;
        try { using var ms = new MemoryStream(bytes); using var img = Image.FromStream(ms); if (img.Width < 64 || img.Height < 64) return false; }   // must decode as a picture
        catch { return false; }
        Directory.CreateDirectory(Dir);
        string name = FileNameFor(baseKey), path = Path.Combine(Dir, name + ".img"), tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        lock (Gate) _have.Add(name);
        if (EmbedLocal && !string.IsNullOrEmpty(t.LocalPath) && !string.IsNullOrEmpty(filePath)) TryEmbed(filePath!, bytes);
        var ui = _ui;
        if (ui is not null && ui.IsHandleCreated && !ui.IsDisposed)
            try { ui.BeginInvoke(new Action(() => Arrived?.Invoke(baseKey))); } catch { }
        return true;
    }

    /// <summary>Write the picture into a PC file's own tag — only when the file has none. A file that is open
    /// for playback (or read-only) just keeps its downloaded copy; nothing is retried, nothing is reported.</summary>
    private static void TryEmbed(string path, byte[] bytes)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            if (f.Tag.Pictures is { Length: > 0 }) return;
            string mime = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "image/jpeg";
            f.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(bytes)) { Type = TagLib.PictureType.FrontCover, MimeType = mime, Description = "Cover" } };
            f.Save();
        }
        catch { /* best-effort */ }
    }
}
