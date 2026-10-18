using System.Buffers.Binary;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Read-only access to the data Encore OS (a custom iPod Video firmware that plays straight from the disk:
/// FLAC in /Music plus every iTunes copy under iPod_Control\Music, registered or not) keeps on the iPod:
///   • /Encore/adatok.bin — its settings text in two 64 KB slots ("ENCSET01", seq, len, CRC32, text):
///     "plays=KEY,COUNT,LAST" lines (KEY = <see cref="PathKey"/>, LAST = minutes since 2000-01-01 local)
///     and "rating=STARS|/path" lines;
///   • /Encore/cache.bin — its library index (2 MB slots after a 4 KB header: "ENCLIB01", seq, len, CRC32,
///     then at +512 the "LIBIDX01" blob: per file size, length, path, title, artist, album).
/// Mixtape NEVER writes either file. Two uses: the Library Doctor tells stray files Encore hides anyway
/// (extra copies of a song kept elsewhere) from the only copy of a song Encore plays, and the device's
/// track list shows Encore's plays (<see cref="ApplyPlays"/>, display only, like <see cref="PlayCounts"/>).
/// The duplicate rule is Encore's own (src/media/library.c, dedupe): same title and album (exact), the
/// same artist (equal, or one is the start of the other followed by a separator: "Lane 8" / "Lane 8;X"),
/// lengths chained within 2 s; the copy Encore keeps is FLAC, then ALAC, then MP3, outside iTunes' folder
/// first, then the alphabetically first path.
/// </summary>
internal sealed class EncoreData
{
    public sealed record Entry(string Path, string Title, string Artist, string Album, int DurationSec, uint Size);

    /// <summary>Every audio file Encore indexed, by its iPod path ("/Music/a.flac", "/iPod_Control/Music/F00/x.m4a").</summary>
    public readonly List<Entry> Index = new();
    /// <summary>Encore's own plays: <see cref="PathKey"/> → (count, last played as minutes since 2000).</summary>
    public readonly Dictionary<uint, (uint Count, uint Last)> Plays = new();
    /// <summary>Stars (1..5) set in Encore, by iPod path.</summary>
    public readonly Dictionary<string, int> Ratings = new(StringComparer.OrdinalIgnoreCase);

    public bool HasIndex => Index.Count > 0;

    /// <summary>Encore's data on the iPod at <paramref name="mountRoot"/>, or null if Encore isn't on it.</summary>
    public static EncoreData? Load(string mountRoot)
    {
        string dir = Path.Combine(mountRoot, "Encore");
        string data = Path.Combine(dir, "adatok.bin"), cache = Path.Combine(dir, "cache.bin");
        if (!File.Exists(data) && !File.Exists(cache)) return null;
        var e = new EncoreData();
        try { if (File.Exists(data)) e.ParseSettings(ReadNewestSlot(data, 0, 65536, "ENCSET01", 20)); } catch { }
        try { if (File.Exists(cache)) e.ParseIndex(ReadNewestSlot(cache, 4096, 2 * 1024 * 1024, "ENCLIB01", 512)); } catch { }
        return e;
    }

    // ---- file formats ------------------------------------------------------------------------------------

    /// <summary>The newest slot whose CRC checks out (two slots, written alternately), or null.</summary>
    private static byte[]? ReadNewestSlot(string path, long baseOff, int slotSize, string magic, int dataOff)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[]? best = null;
        uint bestSeq = 0;
        var hdr = new byte[20];
        for (int slot = 0; slot < 2; slot++)
        {
            long at = baseOff + (long)slot * slotSize;
            if (at + dataOff > fs.Length) continue;
            fs.Position = at;
            if (fs.Read(hdr, 0, 20) != 20 || Encoding.ASCII.GetString(hdr, 0, 8) != magic) continue;
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
            if (len == 0 || len > slotSize - dataOff || at + dataOff + len > fs.Length) continue;
            if (best is not null && (int)(seq - bestSeq) <= 0) continue;
            var buf = new byte[len];
            fs.Position = at + dataOff;
            if (fs.Read(buf, 0, (int)len) != len || Crc32(buf) != crc) continue;
            best = buf;
            bestSeq = seq;
        }
        return best;
    }

    internal static uint Crc32(byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in b)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1)));
        }
        return ~c;
    }

    private void ParseSettings(byte[]? text)
    {
        if (text is null) return;
        foreach (string raw in Encoding.UTF8.GetString(text).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq], v = line[(eq + 1)..];
            if (k == "plays")
            {
                var p = v.Split(',');
                if (p.Length >= 2 && uint.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out uint key)
                    && uint.TryParse(p[1], out uint count) && count > 0)
                    Plays[key] = (count, p.Length >= 3 && uint.TryParse(p[2], out uint last) ? last : 0);
            }
            else if (k == "rating")
            {
                int bar = v.IndexOf('|');
                if (bar > 0 && int.TryParse(v[..bar], out int stars) && stars is >= 1 and <= 5) Ratings[v[(bar + 1)..]] = stars;
            }
        }
    }

    private void ParseIndex(byte[]? b)
    {
        if (b is null || b.Length < 12 || Encoding.ASCII.GetString(b, 0, 8) != "LIBIDX01") return;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        int p = 12;
        var f = new string[5];
        for (uint i = 0; i < n && p + 17 <= b.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
            int dur = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 4));
            p += 17;
            for (int k = 0; k < 5; k++)
            {
                int z = Array.IndexOf(b, (byte)0, p);
                if (z < 0) return;
                f[k] = Encoding.UTF8.GetString(b, p, z - p);
                p = z + 1;
            }
            Index.Add(new Entry(f[0], f[1], f[2], f[3], dur, size));
        }
    }

    // ---- Encore's rules ----------------------------------------------------------------------------------

    /// <summary>Encore's library_path_key: FNV-1a over the UTF-8 path, ASCII lower-cased, '\' as '/'.</summary>
    public static uint PathKey(string ipodPath)
    {
        uint h = 2166136261u;
        foreach (byte b in Encoding.UTF8.GetBytes(ipodPath))
        {
            uint c = b == (byte)'\\' ? '/' : b is >= (byte)'A' and <= (byte)'Z' ? b + 32u : b;
            h = (h ^ c) * 16777619u;
        }
        return h;
    }

    /// <summary>iPod path of a track's Location (":iPod_Control:Music:F00:x.m4a" → "/iPod_Control/Music/F00/x.m4a").</summary>
    public static string IpodPath(string location) => "/" + location.TrimStart(':').Replace(':', '/');

    /// <summary>iPod path of an OS path on the mounted iPod ("H:\Music\a.flac" → "/Music/a.flac").</summary>
    public static string IpodPath(string mountRoot, string osPath)
    {
        string rel = Path.GetRelativePath(mountRoot, osPath).Replace('\\', '/');
        return "/" + rel.TrimStart('/');
    }

    private static bool UnderItunes(string p) => p.StartsWith("/iPod_Control/Music/", StringComparison.OrdinalIgnoreCase);

    private static int Rank(Entry e)
    {
        string ext = Path.GetExtension(e.Path).ToLowerInvariant();
        int c = ext == ".flac" ? 0 : ext is ".m4a" or ".m4b" ? 1 : ext == ".mp3" ? 2 : 3;
        return c * 2 + (UnderItunes(e.Path) ? 1 : 0);
    }

    private static int PathCmp(string a, string b) =>
        string.Compare(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool Better(Entry a, Entry b)
    {
        int ra = Rank(a), rb = Rank(b);
        return ra != rb ? ra < rb : PathCmp(a.Path, b.Path) < 0;
    }

    /// <summary>Encore's dd_artist_same, byte for byte (ASCII-only case folding, like the C code).</summary>
    public static bool ArtistSame(string a, string b)
    {
        if (a == b) return true;
        byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
        byte[] sh = x.Length < y.Length ? x : y, lo = x.Length < y.Length ? y : x;
        int n = sh.Length;
        if (n == 0) return false;
        for (int i = 0; i < n; i++)
        {
            int p = sh[i], q = lo[i];
            if (p is >= 'A' and <= 'Z') p += 32;
            if (q is >= 'A' and <= 'Z') q += 32;
            if (p != q) return false;
        }
        int r = n;
        while (r < lo.Length && lo[r] == ' ') r++;
        string rest = Encoding.UTF8.GetString(lo, r, lo.Length - r);
        return rest.Length > 0 && (";,&/(".IndexOf(rest[0]) >= 0
            || rest.StartsWith("feat", StringComparison.Ordinal) || rest.StartsWith("Feat", StringComparison.Ordinal)
            || rest.StartsWith("ft.", StringComparison.Ordinal) || rest.StartsWith("Ft.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The songs Encore shows only once: each group (2+ copies) lists the copy Encore keeps first. Only
    /// entries passing <paramref name="present"/> take part (a file deleted since Encore's last scan must
    /// not be anyone's keeper).
    /// </summary>
    public List<List<Entry>> DuplicateGroups(Func<Entry, bool> present)
    {
        var groups = new List<List<Entry>>();
        var cands = Index.Where(e => e.Title.Length > 0 && e.Artist.Length > 0 && present(e));
        foreach (var same in cands.GroupBy(e => (e.Title, e.Album)))
        {
            var byLen = same.OrderBy(e => e.DurationSec).ToList();
            int i = 0;
            while (i < byLen.Count)
            {
                int j = i + 1;
                while (j < byLen.Count && byLen[j].DurationSec - byLen[j - 1].DurationSec <= 2) j++;
                // within the length chain: one group per artist; the best copy stays (Encore's dedupe loop)
                var reps = new List<List<Entry>>();
                for (int k = i; k < j; k++)
                {
                    var t = byLen[k];
                    var g = reps.FirstOrDefault(r => ArtistSame(r[0].Artist, t.Artist));
                    if (g is null) { reps.Add(new List<Entry> { t }); continue; }
                    if (Better(t, g[0])) g.Insert(0, t); else g.Add(t);
                }
                groups.AddRange(reps.Where(r => r.Count > 1));
                i = j;
            }
        }
        return groups;
    }

    /// <summary>Encore's "last played" (minutes since 2000-01-01, local) as a DateTime; null for 0.</summary>
    public static DateTime? StampToDate(uint minutes) => minutes == 0 ? null : new DateTime(2000, 1, 1).AddMinutes(minutes);

    /// <summary>
    /// Overlay Encore's plays, last-played dates and stars on the device's track list (display only: the
    /// RawDb is untouched, so a save can't persist or double them). A song Encore shows once collects the
    /// plays of every copy in its group (Encore counts them on the copy it keeps, often a FLAC outside
    /// iTunes' list), credited to one registered copy only. Returns how many tracks changed.
    /// </summary>
    public int ApplyPlays(IEnumerable<Track> tracks, string mountRoot)
    {
        if (Plays.Count == 0 && Ratings.Count == 0) return 0;
        var registered = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
            if (!string.IsNullOrEmpty(t.Location)) registered[IpodPath(t.Location!)] = t;

        // every registered track: its own path first, then the other copies of its song
        var credit = new Dictionary<Track, List<string>>();
        var credited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HasIndex)
            foreach (var g in DuplicateGroups(e => true))
            {
                var reg = g.Select(e => registered.TryGetValue(e.Path, out var t) ? t : null).FirstOrDefault(t => t is not null);
                if (reg is null) continue;
                credit[reg] = g.Select(e => e.Path).ToList();
                foreach (var e in g) credited.Add(e.Path);
            }
        foreach (var (path, t) in registered)
            if (!credited.Contains(path)) credit[t] = new List<string> { path };

        int changed = 0;
        foreach (var (t, paths) in credit)
        {
            uint plays = 0, last = 0;
            int stars = 0;
            foreach (string p in paths)
            {
                if (Plays.TryGetValue(PathKey(p), out var pl)) { plays += pl.Count; last = Math.Max(last, pl.Last); }
                if (stars == 0 && Ratings.TryGetValue(p, out int s)) stars = s;
            }
            if (plays == 0 && stars == 0) continue;
            t.PlayCount += plays;
            if (StampToDate(last) is { } d && (t.LastPlayed is null || d > t.LastPlayed)) t.LastPlayed = d;
            if (stars > 0) t.Rating = (byte)(stars * 20);   // a rating set on the iPod wins, like PlayCounts
            changed++;
        }
        return changed;
    }
}
