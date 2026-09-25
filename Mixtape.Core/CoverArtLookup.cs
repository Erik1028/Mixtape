using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace iPodCommander;

/// <summary>
/// Finds a PUBLIC cover-art URL for a song, so Discord Rich Presence can show a real cover. Discord renders
/// only images it can fetch itself and an iPod's covers are local files, hence a lookup against two public,
/// key-less services: Apple's iTunes Search API, then MusicBrainz + the Cover Art Archive (which carries the
/// releases Apple's catalogue misses — most Hungarian albums, for instance).
///
/// Four rules earn their keep here:
///   • A WRONG cover is worse than none. Candidates are scored against the artist/album asked for and
///     rejected below <see cref="MinScore"/> — otherwise "Imagine Dragons / 50 Greatest Hits" cheerfully
///     matches "Lifehouse / Greatest Hits".
///   • Look an ALBUM up once, not once per song. The album answer is cached under an album-only key, so a
///     16-track record costs one request instead of sixteen. (Getting this wrong is also what triggers the
///     throttling described below.)
///   • A MISS EXPIRES. Apple answers an over-eager client with HTTP 200 and an EMPTY result list — identical
///     to "not in the catalogue" — so a permanent negative cache would let one throttled minute blank the
///     covers of a whole library forever. Misses are stored with a timestamp and retried after
///     <see cref="MissRetry"/>; a miss caused by an exception is not cached at all.
///   • Never disturb playback. Offline, a timeout, a proxy, malformed JSON: all yield null.
///
/// Call it from a background thread — it blocks.
/// </summary>
public static class CoverArtLookup
{
    private const double MinScore = 0.45;
    /// <summary>A miss we BELIEVE: the service answered with candidates, none of which matched.</summary>
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);
    /// <summary>A miss we DOUBT: the service returned an empty list, which is also exactly what Apple sends
    /// (HTTP 200!) to a client it is throttling. Retried soon, so one busy minute cannot blank a library.</summary>
    private static readonly TimeSpan EmptyRetry = TimeSpan.FromHours(6);
    /// <summary>Politeness gap between calls to one service (MusicBrainz asks for =&lt;1/s; Apple throttles
    /// silently, and staying under a few per second keeps us far away from it).</summary>
    private static readonly TimeSpan AppleGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BrainzGap = TimeSpan.FromMilliseconds(1100);

    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();          // guards the cache only — never held across the network
    private static readonly object NetGate = new();       // serialises outbound calls + their politeness delay
    private static Dictionary<string, string>? _cache;
    private static DateTime _lastApple = DateTime.MinValue, _lastBrainz = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        // MusicBrainz requires a descriptive User-Agent; Apple simply likes one.
        c.DefaultRequestHeaders.Add("User-Agent", "Mixtape/1.0 (+https://github.com/Erik1028/Mixtape)");
        return c;
    }

    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixtape", "covers.json");

    // Cache entries: a URL for a hit, or "!<unix seconds>" for a miss that may be retried later.
    private static string AlbumKey(string artist, string album) => "A\u001f" + artist + "\u001f" + album;
    private static string SongKey(string artist, string title) => "S\u001f" + artist + "\u001f" + title;

    /// <summary>
    /// A public https cover URL for this song, or null when nothing trustworthy was found. The ALBUM is
    /// tried first (the right cover, and the answer is shared by every track on it); failing that the SONG,
    /// which still yields correct art for singles and for records listed under another title.
    /// Blocking; safe to call repeatedly.
    /// </summary>
    public static string? Find(string? artist, string? album, string? title = null)
    {
        string primary = PrimaryArtist(artist);          // used for the SEARCH TERM and the cache keys
        string full = (artist ?? "").Trim();              // used for SCORING — truncating it can rank a
                                                          // same-prefix band above the real one
        string alb = (album ?? "").Trim(), ttl = (title ?? "").Trim();
        if (alb.Length == 0 && ttl.Length == 0) return null;

        // ---- album level: one answer for the whole record ----
        if (alb.Length > 0)
        {
            string key = AlbumKey(primary, alb);
            var (known, url) = Lookup(key);
            if (known) { if (url is not null) return url; }
            else
            {
                bool failed = false, sawAny = false;
                string? found = null;
                try
                {
                    found = QueryItunes(primary, alb, "album", alb, full, out sawAny);
                    found ??= QueryMusicBrainz(primary, alb);
                }
                catch { failed = true; }                       // network/parse trouble: do NOT record a miss
                if (!failed) Store(key, found, sawAny);
                if (found is not null) return found;
            }
        }

        // ---- song level: only reached when the album gave nothing ----
        if (ttl.Length > 0)
        {
            string key = SongKey(primary, ttl);
            var (known, url) = Lookup(key);
            if (known) return url;
            bool failed = false, sawAny = false;
            string? found = null;
            try { found = QueryItunes(primary, ttl, "song", ttl, full, out sawAny); }
            catch { failed = true; }
            if (!failed) Store(key, found, sawAny);
            return found;
        }
        return null;
    }

    /// <summary>The cached answer without touching the network: <c>known</c> false means "never looked up
    /// (or the miss expired)". Lets a caller show its card instantly and fill the cover in afterwards.</summary>
    public static bool TryGetCached(string? artist, string? album, string? title, out string? url)
    {
        string primary = PrimaryArtist(artist);
        string alb = (album ?? "").Trim(), ttl = (title ?? "").Trim();

        if (alb.Length > 0)
        {
            var (known, hit) = Lookup(AlbumKey(primary, alb));
            if (known && hit is not null) { url = hit; return true; }      // a cached cover
            if (!known) { url = null; return false; }                       // never asked → caller should resolve
            // album is a known miss: the song-level answer decides
        }
        if (ttl.Length > 0)
        {
            var (known, hit) = Lookup(SongKey(primary, ttl));
            url = hit;
            return known;
        }
        url = null;
        return alb.Length > 0;   // album miss and no title to fall back on → settled, no cover
    }

    /// <summary>(known, url). known=false means "not cached, or the stored miss has expired".</summary>
    private static (bool known, string? url) Lookup(string key)
    {
        lock (Gate)
        {
            LoadCacheLocked();
            if (!_cache!.TryGetValue(key, out var v)) return (false, null);
            if (v.Length == 0) return (false, null);                        // legacy permanent miss → re-ask once
            if (v[0] != '!' && v[0] != '?') return (true, v);                // a hit
            if (!long.TryParse(v.AsSpan(1), out long when)) return (false, null);
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(when);
            var life = v[0] == '!' ? MissRetry : EmptyRetry;                 // believed vs doubted miss
            return age < life ? (true, null) : (false, null);                // fresh miss stands; stale one is re-asked
        }
    }

    /// <param name="sawCandidates">The service returned SOMETHING to score. False marks a doubtful miss
    /// (an empty answer, possibly throttling) which expires far sooner.</param>
    private static void Store(string key, string? url, bool sawCandidates)
    {
        lock (Gate)
        {
            LoadCacheLocked();
            _cache![key] = url ?? ((sawCandidates ? "!" : "?") + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            SaveCacheLocked();
        }
    }

    /// <summary>"Azahriah, DESH" and "X feat. Y" search far better as just the first artist. Separators are
    /// matched with surrounding spaces (bar the comma) so names like "Jay-Z" or "MGMT x" survive intact.</summary>
    private static string PrimaryArtist(string? artist)
    {
        string a = (artist ?? "").Trim();
        if (a.Length == 0) return "";
        foreach (var sep in new[] { ", ", "; ", " & ", " feat. ", " feat ", " ft. ", " ft ", " vs. ", " vs " })
        {
            int i = a.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    /// <param name="entity">"album" or "song".</param>
    /// <param name="want">The name candidates are scored against (album title, or track title).</param>
    /// <param name="scoreArtist">The artist as tagged (untruncated) — candidates are ranked against this,
    /// while <paramref name="artist"/> (the truncated primary) only builds the search term.</param>
    private static string? QueryItunes(string artist, string query, string entity, string want,
                                       string scoreArtist, out bool sawAny)
    {
        sawAny = false;
        string term = Uri.EscapeDataString((artist + " " + query).Trim());
        string body = Get($"https://itunes.apple.com/search?term={term}&entity={entity}&limit=5", apple: true);
        if (JsonNode.Parse(body) is not JsonObject root || root["results"] is not JsonArray results) return null;
        sawAny = results.Count > 0;   // an EMPTY list may just mean "throttled" — the caller shortens the miss

        double bestScore = 0; string? bestUrl = null, bestName = null;
        foreach (var node in results)
        {
            if (node is not JsonObject o) continue;
            string gotName = (entity == "song" ? o["trackName"] : o["collectionName"])?.GetValue<string>() ?? "";
            string gotArtist = o["artistName"]?.GetValue<string>() ?? "";
            string art = o["artworkUrl100"]?.GetValue<string>() ?? "";
            if (art.Length == 0) continue;

            // The name carries most of the signal; the artist guards against a same-titled record by
            // someone else. Score the artist as TAGGED, and fall back to the truncated form when that is
            // all we have.
            double nameSim = Similarity(want, gotName);
            string mine = scoreArtist.Length > 0 ? scoreArtist : artist;
            double artistSim = Math.Max(Similarity(mine, gotArtist), Similarity(artist, gotArtist));

            // A NAME-ONLY match is exactly what "Greatest Hits" by the wrong band looks like — and 0.65
            // alone already clears MinScore. When we know who the artist is, demand some evidence of them.
            if (mine.Length > 0 && artistSim <= 0) continue;

            double s = 0.65 * nameSim + 0.35 * artistSim;
            if (s > bestScore) { bestScore = s; bestUrl = art; bestName = gotName; }
        }
        if (bestUrl is null || bestScore < MinScore) return null;
        // No artist at all: the title is the ONLY signal, so require it to be essentially exact.
        if (scoreArtist.Length == 0 && artist.Length == 0 && Similarity(want, bestName ?? "") < 0.9) return null;
        return bestUrl.Replace("100x100bb", "512x512bb");   // the same asset at a size worth showing
    }

    /// <summary>MusicBrainz release search, then the Cover Art Archive image for the winning release. Returns
    /// a DIRECT image URL (not the /front redirect), so Discord's fetcher gets a plain 200.</summary>
    private static string? QueryMusicBrainz(string artist, string album)
    {
        // The Lucene query is a HINT, not a filter: MusicBrainz happily returns same-titled releases by
        // other artists. Without an artist there is nothing to guard a title match with, so don't guess.
        if (artist.Length == 0) return null;
        string q = Uri.EscapeDataString($"artist:\"{artist}\" AND release:\"{album}\"");
        string body = Get($"https://musicbrainz.org/ws/2/release/?query={q}&fmt=json&limit=5", apple: false);
        if (JsonNode.Parse(body) is not JsonObject root || root["releases"] is not JsonArray releases) return null;

        foreach (var node in releases)
        {
            if (node is not JsonObject rel) continue;
            if (Similarity(album, rel["title"]?.GetValue<string>() ?? "") < 0.6) continue;   // the search is fuzzy
            // …and the credited artist has to be ours, or a same-titled record by anyone would win.
            var credit = new StringBuilder();
            if (rel["artist-credit"] is JsonArray ac)
                foreach (var a in ac)
                    if (a is JsonObject ao) credit.Append((ao["name"] ?? ao["artist"]?["name"])?.GetValue<string>() ?? "").Append(' ');
            if (Similarity(artist, credit.ToString()) < 0.5) continue;
            string? mbid = rel["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(mbid)) continue;
            try
            {
                string caa = Get($"https://coverartarchive.org/release/{mbid}", apple: false);
                if (JsonNode.Parse(caa) is not JsonObject c || c["images"] is not JsonArray imgs) continue;
                foreach (var i in imgs)
                {
                    if (i is not JsonObject img || img["front"]?.GetValue<bool>() != true) continue;
                    string? big = img["thumbnails"]?["500"]?.GetValue<string>()
                               ?? img["thumbnails"]?["large"]?.GetValue<string>()
                               ?? img["image"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(big)) return big!.Replace("http://", "https://");
                }
            }
            catch { /* this release has no art — try the next candidate */ }
        }
        return null;
    }

    /// <summary>A plain GET with a per-service politeness gap. The delay is taken on NetGate, never on the
    /// cache lock, so a waiting lookup can't block a caller that only wants a cached answer.</summary>
    private static string Get(string url, bool apple)
    {
        lock (NetGate)
        {
            var last = apple ? _lastApple : _lastBrainz;
            var gap = apple ? AppleGap : BrainzGap;
            var wait = last + gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            if (apple) _lastApple = DateTime.UtcNow; else _lastBrainz = DateTime.UtcNow;
        }
        return Http.GetStringAsync(url).GetAwaiter().GetResult();
    }

    /// <summary>Token overlap of two names after accent/punctuation folding — enough to tell "Viharok" from
    /// "Greatest Hits" without pulling in a fuzzy-matching dependency.</summary>
    private static double Similarity(string a, string b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        int both = ta.Count(t => tb.Contains(t));
        return (double)both / Math.Max(ta.Count, tb.Count);
    }

    private static HashSet<string> Tokens(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in (s ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;   // fold accents
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return new HashSet<string>(sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    private static void LoadCacheLocked()
    {
        if (_cache is not null) return;
        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(CacheFile) && JsonNode.Parse(File.ReadAllText(CacheFile)) is JsonObject o)
                foreach (var kv in o)
                    if (kv.Value is not null) _cache[kv.Key] = kv.Value.GetValue<string>();
        }
        catch { /* a corrupt cache is not worth a failure — start empty */ }
    }

    private static void SaveCacheLocked()
    {
        try
        {
            var o = new JsonObject();
            foreach (var kv in _cache!) o[kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            // Write-then-replace: a crash mid-save must not leave a truncated cache behind.
            string tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            if (File.Exists(CacheFile)) File.Replace(tmp, CacheFile, null); else File.Move(tmp, CacheFile);
        }
        catch { /* best-effort: worst case we look the album up again next launch */ }
    }
}
