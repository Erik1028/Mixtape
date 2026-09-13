using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace iPodCommander;

/// <summary>One word of a lyric line and the moment it starts (enhanced-LRC word timing).</summary>
public readonly record struct WordStamp(TimeSpan At, string Word);

/// <summary>One sheet LRCLIB holds for a song — a particular release, with its own timings. Several exist
/// for most songs (the album cut, a single edit, a compilation), which is exactly why a sheet can be a
/// second or two out against the copy the listener actually owns.</summary>
public readonly record struct LyricsCandidate(
    string Title, string Artist, string Album, TimeSpan Duration, bool Synced, int Lines, string Text);

/// <summary>One lyric line and the moment it is sung. <see cref="Words"/> is filled only when the source
/// carried per-word timings (enhanced LRC); otherwise the panel spreads the line across its own duration.</summary>
public readonly record struct LyricLine(TimeSpan At, string Text, IReadOnlyList<WordStamp>? Words = null);

/// <summary>
/// Time-synced lyrics for the song that is playing — the Apple-Music-style panel where the current line
/// lights up and clicking a line jumps there.
///
/// Three sources, cheapest and most trustworthy first:
///   1. A <c>.lrc</c> file sitting next to the audio (the user's own, always right, works offline).
///   2. Lyrics embedded in the file's tags — synchronised (ID3 SYLT) if present, otherwise the plain
///      unsynchronised text, which still reads fine as a scrolling sheet.
///   3. LRCLIB (lrclib.net), a public, key-less, community lyrics database. Opt-in, since it is the only
///      part that leaves the machine, and asked ONLY when the user actually opens the lyrics panel.
///
/// Answers are cached as plain .lrc files under %APPDATA%\Mixtape\lyrics — inspectable and editable by
/// hand, and a "nothing found" is remembered too (with an expiry, so a bad network minute isn't permanent).
/// Everything is best-effort: any failure yields an empty list. Call it off the UI thread — it blocks.
/// </summary>
public static class LyricsLookup
{
    /// <summary>A miss expires, so an offline session doesn't blank a song forever.</summary>
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(3);
    private static readonly HttpClient Http = CreateClient();
    private static readonly object NetGate = new();
    private static DateTime _lastCall = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        // A lyrics sheet is a few KB; cap the buffer so a misconfigured proxy or a hostile reply cannot
        // stream an unbounded body into memory. Over the cap the read throws, which we treat as a failure.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        c.DefaultRequestHeaders.Add("User-Agent", "Mixtape/1.0 (+https://github.com/fgs8z2n9qh-tech/Mixtape)");
        return c;
    }

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixtape", "lyrics");

    /// <summary>
    /// Lyrics for a song. <paramref name="filePath"/> (when the audio is reachable) unlocks the offline
    /// sources; <paramref name="online"/> gates LRCLIB. Returns an empty list when nothing was found.
    /// <paramref name="synced"/> says whether the lines carry real timings — an unsynced sheet is returned
    /// with all-zero times so the caller can still show it, just without highlighting.
    /// </summary>
    public static IReadOnlyList<LyricLine> Find(string? artist, string? title, TimeSpan duration,
                                                string? filePath, bool online, out bool synced)
    {
        synced = false;
        string art = (artist ?? "").Trim(), ttl = (title ?? "").Trim();
        if (ttl.Length == 0) return Array.Empty<LyricLine>();

        string cache = Path.Combine(CacheDir, Key(art, ttl, duration) + ".lrc");
        // A sheet the listener PICKED in the panel outranks even a .lrc sitting next to the audio: choosing
        // a version is a later and far more specific decision than a file they may have forgotten is there.
        // Without this the "other lyrics" button would silently do nothing for such a song.
        bool chosen = false;
        try { chosen = File.Exists(cache + ".chosen"); } catch { /* unreadable → treat as not chosen */ }

        // 1) a hand-made .lrc beside the audio otherwise wins
        if (!chosen && !string.IsNullOrEmpty(filePath))
        {
            try
            {
                string lrc = Path.ChangeExtension(filePath, ".lrc");
                if (File.Exists(lrc))
                {
                    var own = Parse(File.ReadAllText(lrc), duration, out synced);
                    if (own.Count > 0) return own;
                }
            }
            catch { /* unreadable → fall through */ }
        }

        // 2) the cache (a previous online answer, a remembered miss, or the version the listener chose)
        try
        {
            if (File.Exists(cache))
            {
                string text = File.ReadAllText(cache);
                if (text.StartsWith("#none", StringComparison.Ordinal))
                {
                    // "#none <unix seconds>" — a remembered miss; re-ask once it goes stale.
                    var parts = text.Split(' ', 2);
                    if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out long when)
                        && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(when) < MissRetry)
                        return Array.Empty<LyricLine>();
                }
                else
                {
                    var hit = Parse(text, duration, out synced);
                    if (hit.Count > 0) return hit;
                }
            }
        }
        catch { /* corrupt cache entry → just look it up again */ }

        // 3) tags embedded in the file itself
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                using var f = TagLib.File.Create(filePath);
                string? embedded = f.Tag.Lyrics;
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    var tagged = Parse(embedded!, duration, out synced);
                    if (tagged.Count > 0) return tagged;
                }
            }
            catch { /* not a tagged format, or locked → carry on */ }
        }

        // 4) LRCLIB — only with the user's consent
        if (!online || art.Length == 0) return Array.Empty<LyricLine>();
        try
        {
            string? text = QueryLrclib(art, ttl, duration);
            Save(cache, text);
            if (text is not null)
            {
                var got = Parse(text, duration, out synced);
                if (got.Count > 0) return got;
            }
        }
        catch { /* offline / blocked: do NOT record a miss, so it retries next time */ }
        return Array.Empty<LyricLine>();
    }

    /// <summary>True when this song's lyrics are already on disk, so the caller can show them with no wait
    /// (and know whether opening the panel will need the network).</summary>
    public static bool IsCached(string? artist, string? title, TimeSpan duration, string? filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(Path.ChangeExtension(filePath, ".lrc"))) return true;
            return File.Exists(Path.Combine(CacheDir, Key((artist ?? "").Trim(), (title ?? "").Trim(), duration) + ".lrc"));
        }
        catch { return false; }
    }

    private static string? QueryLrclib(string artist, string title, TimeSpan duration)
    {
        var q = new StringBuilder("https://lrclib.net/api/get?artist_name=")
            .Append(Uri.EscapeDataString(artist))
            .Append("&track_name=").Append(Uri.EscapeDataString(title));
        if (duration > TimeSpan.Zero) q.Append("&duration=").Append((int)Math.Round(duration.TotalSeconds));

        Throttle();

        HttpResponseMessage res;
        try { res = Http.GetAsync(q.ToString()).GetAwaiter().GetResult(); }
        catch { throw; }
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // a genuine "we don't have it"
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"lrclib {(int)res.StatusCode}");

        string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (JsonNode.Parse(body) is not JsonObject o) return null;
        string? syncedText = o["syncedLyrics"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(syncedText)) return syncedText;
        string? plain = o["plainLyrics"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(plain) ? null : plain;
    }

    /// <summary>
    /// Every sheet LRCLIB has for this song, so the listener can pick a different one when the timings
    /// belong to another cut of the recording. Ordered by how close each candidate's length is to the
    /// track being played — the nearest is almost always the right one — with timed sheets before untimed.
    /// Blocks; call it off the UI thread. Returns an empty list on any failure.
    /// </summary>
    public static IReadOnlyList<LyricsCandidate> Search(string? artist, string? title, TimeSpan duration)
    {
        string art = (artist ?? "").Trim(), ttl = (title ?? "").Trim();
        if (ttl.Length == 0) return Array.Empty<LyricsCandidate>();
        try
        {
            var q = new StringBuilder("https://lrclib.net/api/search?track_name=")
                .Append(Uri.EscapeDataString(ttl));
            if (art.Length > 0) q.Append("&artist_name=").Append(Uri.EscapeDataString(art));
            Throttle();

            var res = Http.GetAsync(q.ToString()).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) return Array.Empty<LyricsCandidate>();
            if (JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult()) is not JsonArray arr)
                return Array.Empty<LyricsCandidate>();

            var found = new List<LyricsCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                if (o["instrumental"]?.GetValue<bool>() == true) continue;
                string? synced = Str(o, "syncedLyrics");
                string? plainText = Str(o, "plainLyrics");
                string? text = synced ?? plainText;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var parsed = Parse(text, out bool isSynced);
                if (parsed.Count == 0) continue;
                double secs = o["duration"]?.GetValue<double>() ?? 0;
                // One row per distinct sheet: LRCLIB carries the same words filed under several albums, and
                // a list of six identical choices helps nobody.
                if (!seen.Add(isSynced + "|" + text.Length + "|" + (int)Math.Round(secs))) continue;

                found.Add(new LyricsCandidate(
                    Str(o, "trackName") ?? ttl, Str(o, "artistName") ?? art, Str(o, "albumName") ?? "",
                    TimeSpan.FromSeconds(secs), isSynced, parsed.Count, text));
            }

            // Nearest length wins, and a timed sheet always beats an untimed one of the same closeness.
            found.Sort((a, b) =>
            {
                if (a.Synced != b.Synced) return a.Synced ? -1 : 1;
                double da = duration > TimeSpan.Zero ? Math.Abs((a.Duration - duration).TotalSeconds) : 0;
                double db = duration > TimeSpan.Zero ? Math.Abs((b.Duration - duration).TotalSeconds) : 0;
                return da.CompareTo(db);
            });
            return found.Count > 8 ? found.GetRange(0, 8) : found;
        }
        catch { return Array.Empty<LyricsCandidate>(); }
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonNode n && n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : null;

    /// <summary>Make one of <see cref="Search"/>'s candidates this song's sheet from now on, by writing it
    /// into the cache the ordinary lookup reads first. The user's choice therefore survives a restart and
    /// costs no further network.</summary>
    public static void Adopt(string? artist, string? title, TimeSpan duration, string text)
    {
        string art = (artist ?? "").Trim(), ttl = (title ?? "").Trim();
        if (ttl.Length == 0 || string.IsNullOrWhiteSpace(text)) return;
        string path = Path.Combine(CacheDir, Key(art, ttl, duration) + ".lrc");
        Save(path, text);
        // The marker says "a person chose this", which is what lets it outrank a local .lrc in Find.
        try { File.WriteAllText(path + ".chosen", ""); } catch { /* best-effort */ }
    }

    /// <summary>One call at a time, with a politeness gap — LRCLIB is a free community service.</summary>
    private static void Throttle()
    {
        lock (NetGate)
        {
            var wait = _lastCall.AddMilliseconds(300) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            _lastCall = DateTime.UtcNow;
        }
    }

    private static void Save(string path, string? text)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            // Write-then-replace: two lookups can finish at once (a fast skip through tracks), and a
            // half-written file would come back as a mangled sheet on the next play.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text ?? ("#none " + DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Parse LRC text. Handles several timestamps on one line, the [offset:] tag, and plain text
    /// (returned as untimed lines). Metadata tags like [ar:] are skipped.</summary>
    public static IReadOnlyList<LyricLine> Parse(string text, out bool synced)
        => Parse(text, TimeSpan.Zero, out synced);

    /// <summary>
    /// Parse LRC text, using the track's length to settle the one dialect that is genuinely ambiguous.
    ///
    /// "[00:12:50]" can mean 12.50 seconds (the legacy LRC form, where the hundredths follow a colon) or
    /// 12 minutes 50 seconds (hh:mm:ss). For songs it is always the former, so that is what we assume — but
    /// an iPod also carries audiobooks, podcasts and concert rips, where hh:mm:ss is real. So: parse it the
    /// song way, and if the resulting sheet stops before halfway through a track that the OTHER reading
    /// fits, take the other reading instead. A sheet with no such timestamps parses identically both ways,
    /// so this can never disturb an ordinary file.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string text, TimeSpan duration, out bool synced)
    {
        var lines = ParseCore(text, hoursForm: false, out synced);
        if (duration > TimeSpan.Zero && synced && lines.Count > 0 && lines[^1].At < duration * 0.5)
        {
            var alt = ParseCore(text, hoursForm: true, out bool altSynced);
            if (altSynced && alt.Count == lines.Count && alt[^1].At > lines[^1].At
                && alt[^1].At <= duration + TimeSpan.FromSeconds(30))
            {
                synced = altSynced;
                return alt;
            }
        }
        return lines;
    }

    private static IReadOnlyList<LyricLine> ParseCore(string text, bool hoursForm, out bool synced)
    {
        var timed = new List<LyricLine>();
        var plain = new List<string>();
        TimeSpan offset = TimeSpan.Zero;

        // Split on either terminator: CRLF leaves an empty entry that the length check below eats, and a
        // lone CR (an old tagger, or an ID3 lyrics frame handed through verbatim) no longer swallows the
        // whole sheet into one line.
        foreach (string raw in text.Split('\n', '\r'))
        {
            // Trim BOTH ends: an indented "  [00:12.00] text" is still a timed line, and dropping it
            // silently turns a synced sheet into an unsynced one.
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;

            int scan = 0;
            var stamps = new List<TimeSpan>();
            while (scan < line.Length)
            {
                // "[00:30.00] [01:45.00]Chorus" — writers do put a space between compressed repeats.
                int look = scan;
                while (look < line.Length && line[look] == ' ') look++;
                if (look >= line.Length || line[look] != '[') break;
                int close = line.IndexOf(']', look);
                if (close < 0) break;
                string tag = line[(look + 1)..close];

                // Decide BEFORE consuming. A bracket that is not a tag belongs to the lyrics — a section
                // marker or an ad-lib, "[00:12.00] [Chorus]" or "[00:14.00] [chuckles] real words" — and
                // swallowing it left an empty line that then looked exactly like an instrumental beat.
                // (A metadata tag IS consumed, which is what makes its line disappear.)
                bool isTag = TryStamp(tag, hoursForm, out var at)
                          || tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                          || (stamps.Count == 0 && tag.Contains(':'));
                if (!isTag) break;

                scan = close + 1;
                if (TryStamp(tag, hoursForm, out at)) stamps.Add(at);
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(tag[7..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                    offset = TimeSpan.FromMilliseconds(ms);
            }

            string body = line[scan..].Trim();

            // Enhanced LRC: the body may carry <mm:ss.xx> before each word. Pull those out, keeping the
            // plain text intact so a panel that ignores word timings still renders correctly.
            List<WordStamp>? words = null;
            if (body.Contains('<') && body.Contains('>'))
            {
                var plainText = new StringBuilder();
                var got = new List<WordStamp>();
                int i2 = 0;
                while (i2 < body.Length)
                {
                    if (body[i2] == '<')
                    {
                        int close = body.IndexOf('>', i2);
                        if (close > i2 && TryStamp(body[(i2 + 1)..close], hoursForm, out var wAt))
                        {
                            int wordStart = close + 1;
                            int next = body.IndexOf('<', wordStart);
                            string w = (next < 0 ? body[wordStart..] : body[wordStart..next]);
                            if (w.Trim().Length > 0) got.Add(new WordStamp(wAt, w.Trim()));
                            plainText.Append(w);
                            i2 = next < 0 ? body.Length : next;
                            continue;
                        }
                    }
                    plainText.Append(body[i2]);
                    i2++;
                }
                if (got.Count > 0) { words = got; body = plainText.ToString().Trim(); }
            }

            if (stamps.Count > 0)
            {
                // A timestamp with no words is a musical interlude — keep it as a blank beat so the
                // highlight travels through it instead of resting on the previous line.
                //
                // The offset is SUBTRACTED: in the LRC format a positive [offset:] makes the lyrics appear
                // SOONER, i.e. it moves the timestamps earlier. Adding it moved every offset-tagged sheet
                // the wrong way, by twice the tag.
                //
                // A line may carry SEVERAL timestamps (a chorus written once and repeated). Word stamps are
                // absolute times belonging to the FIRST occurrence, so each repeat gets them re-based onto
                // its own start — otherwise every repeat but the first showed up as already sung.
                // NOT sorted: enhanced-LRC word stamps are absolute times belonging to the occurrence the
                // tagger wrote them next to, which is the FIRST tag on the line in file order. (The whole
                // sheet is sorted at the end anyway, so nothing here needs to be in time order.)
                TimeSpan first = stamps[0];
                foreach (var at in stamps)
                {
                    TimeSpan lineAt = at - offset;
                    List<WordStamp>? mine = null;
                    if (words is not null)
                    {
                        mine = new List<WordStamp>(words.Count);
                        foreach (var w in words) mine.Add(new WordStamp(w.At - first + lineAt, w.Word));
                    }
                    timed.Add(new LyricLine(lineAt, body, mine));
                }
            }
            else if (body.Length > 0 && !body.StartsWith('[')) plain.Add(body);
        }

        if (timed.Count > 0)
        {
            synced = true;
            // A STABLE sort: List.Sort is an introsort, and a sheet where several lines share one timestamp
            // (a backing vocal under its line, a run of blank leaders) came back permuted — often almost
            // reversed. OrderBy keeps file order within a tie, which is the order they were written to be
            // read in.
            return timed.OrderBy(l => l.At).ToList();
        }
        synced = false;
        return plain.Select(t => new LyricLine(TimeSpan.Zero, t)).ToList();
    }

    private static bool TryStamp(string tag, bool hoursForm, out TimeSpan at)
    {
        at = TimeSpan.Zero;
        // A comma where the decimal point belongs: a tagger that formatted the number with the machine's
        // own separator (routine on a Hungarian or German PC). Every parse below is InvariantCulture with
        // no thousands grouping allowed, so a comma can only ever have meant a decimal point.
        tag = tag.Replace(',', '.');

        // mm:ss, mm:ss.xx or mm:ss.xxx  (and hh:mm:ss.xx for very long files)
        var bits = tag.Split(':');
        if (bits.Length is < 2 or > 3) return false;

        // The LEGACY form "[mm:ss:xx]" puts the fraction after a COLON. Read as hh:mm:ss it turns
        // [00:12:50] into 770 seconds — the line lands minutes into the future and the sheet looks
        // scrambled. Three fields with no decimal point anywhere is that form, UNLESS the caller has
        // worked out from the track's length that this sheet really is hh:mm:ss (see Parse).
        if (bits.Length == 3 && !hoursForm && !tag.Contains('.'))
        {
            string frac = bits[2].Trim();
            if (frac.Length is 0 or > 3) return false;
            foreach (char ch in frac) if (ch is < '0' or > '9') return false;   // no sign: the width IS the scale
            if (!double.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out double lm)) return false;
            if (!double.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out double ls)) return false;
            if (!double.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out double lf)) return false;
            if (lm < 0 || ls < 0 || ls >= 60) return false;
            // Scale by DIGITS, so ":5" is five tenths — the same value ".5" means one line further down.
            at = TimeSpan.FromSeconds(lm * 60 + ls + lf / Math.Pow(10, frac.Length));
            return at < TimeSpan.FromHours(12);
        }

        double h = 0, m, s;
        if (bits.Length == 3)
        {
            if (!double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return false;
            if (!double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return false;
        }
        else
        {
            if (!double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return false;
        }
        // Seconds must be a real seconds field in the 3-part form; the 2-part form is left tolerant
        // because some writers put a raw total there ("[00:75.20]"), which is still unambiguous.
        if (m < 0 || s < 0 || h < 0) return false;
        if (bits.Length == 3 && (s >= 60 || m >= 60)) return false;
        at = TimeSpan.FromSeconds(h * 3600 + m * 60 + s);
        return at >= TimeSpan.Zero && at < TimeSpan.FromHours(12);
    }

    /// <summary>A filesystem-safe cache name. Duration is part of it so a different edit/remix of the same
    /// title doesn't inherit the wrong timings.</summary>
    private static string Key(string artist, string title, TimeSpan duration)
    {
        string raw = artist.ToLowerInvariant() + "\u001f" + title.ToLowerInvariant() + "\u001f" + (int)duration.TotalSeconds;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(24);
        for (int i = 0; i < 10; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>The identity a per-song setting (the user's own sync nudge) is filed under. Unlike the
    /// cache name this deliberately IGNORES the track length: the length we know changes by a second
    /// depending on whether the decoder has opened the file yet, and an adjustment that quietly detached
    /// itself from its song would be worse than no adjustment at all.</summary>
    public static string SongKey(string? artist, string? title)
    {
        string raw = Norm(artist) + "\u001f" + Norm(title);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(20);
        for (int i = 0; i < 10; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>The project's identity spelling: NFC first, because the same accented Hungarian title can be
    /// stored decomposed by one program and precomposed by another — byte-different, eye-identical — then
    /// lower-cased with runs of whitespace collapsed. Mirrors MainForm.NormKey, which guards duplicates.</summary>
    private static string Norm(string? s)
    {
        s = (s ?? "").Trim();
        try { s = s.Normalize(NormalizationForm.FormC); } catch { /* lone surrogate → take it as it is */ }
        s = s.ToLowerInvariant();
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first one.
    /// Lines are sorted, so this is a binary search — it runs on every UI tick.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].At <= position) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }
}
