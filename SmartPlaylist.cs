using System.Globalization;

namespace iPodCommander;

/// <summary>
/// The smart-playlist rule engine. Rules live in <see cref="AppSettings"/> (per device); here we turn a
/// <see cref="SmartPlaylistDef"/> + the library's audio tracks into the ordered member list that gets written
/// to a normal iPod playlist. Pure logic, no UI — shared by the editor dialog (field/op metadata + live count)
/// and by MainForm (evaluate → set members). Nothing here touches the iTunesDB, so a bad rule can never corrupt it.
/// </summary>
internal static class SmartPlaylist
{
    public enum FieldType { Text, Number, Rating, Days }

    /// <summary>A filterable track field. Text fields read a string; the others read a number (Rating in stars
    /// 0–5; Days = how many days ago, null = never).</summary>
    public sealed record FieldDef(string Key, string Label, FieldType Type, Func<Track, string?>? Text, Func<Track, double?>? Num);

    public static readonly FieldDef[] Fields =
    {
        new("Artist",      "Artist",       FieldType.Text,   t => t.Artist,      null),
        new("Album",       "Album",        FieldType.Text,   t => t.Album,       null),
        new("AlbumArtist", "Album artist", FieldType.Text,   t => t.AlbumArtist, null),
        new("Title",       "Title",        FieldType.Text,   t => t.Title,       null),
        new("Genre",       "Genre",        FieldType.Text,   t => t.Genre,       null),
        new("Composer",    "Composer",     FieldType.Text,   t => t.Composer,    null),
        new("Comment",     "Comment",      FieldType.Text,   t => t.Comment,     null),
        new("Year",        "Year",         FieldType.Number, null, t => t.Year),
        new("Rating",      "Rating",       FieldType.Rating, null, t => t.Rating / 20.0),
        new("PlayCount",   "Play count",   FieldType.Number, null, t => t.PlayCount),
        new("Bitrate",     "Bitrate",      FieldType.Number, null, t => t.Bitrate),
        new("DateAdded",   "Date added",   FieldType.Days,   null, t => DaysAgo(t.DateAdded)),
        new("LastPlayed",  "Last played",  FieldType.Days,   null, t => DaysAgo(t.LastPlayed)),
    };

    public sealed record OpDef(string Key, string Label);

    /// <summary>The operators valid for a field type (the first is the default).</summary>
    public static OpDef[] OpsFor(FieldType t) => t switch
    {
        FieldType.Text => new[]
        {
            new OpDef("contains", "contains"), new OpDef("notcontains", "does not contain"),
            new OpDef("is", "is"), new OpDef("isnot", "is not"), new OpDef("startswith", "starts with"),
        },
        FieldType.Days => new[] { new OpDef("within", "in the last"), new OpDef("notwithin", "not in the last") },
        _ => new[]   // Number + Rating
        {
            new OpDef("atleast", "at least"), new OpDef("atmost", "at most"),
            new OpDef("is", "is"), new OpDef("isnot", "is not"),
            new OpDef("more", "more than"), new OpDef("less", "less than"),
        },
    };

    public sealed record SortDef(string Key, string Label);
    public static readonly SortDef[] Sorts =
    {
        new("Added", "Recently added"), new("Title", "Title (A–Z)"), new("Artist", "Artist (A–Z)"),
        new("Album", "Album (A–Z)"), new("MostPlayed", "Most played"), new("LeastPlayed", "Least played"),
        new("HighestRated", "Highest rated"), new("RecentlyPlayed", "Recently played"), new("Random", "Random"),
    };

    public static FieldDef Field(string key) => Array.Find(Fields, f => f.Key == key) ?? Fields[0];
    public static string OpLabel(FieldType t, string key) => Array.Find(OpsFor(t), o => o.Key == key)?.Label ?? key;
    public static string SortLabel(string key) => Array.Find(Sorts, s => s.Key == key)?.Label ?? Sorts[0].Label;

    /// <summary>Evaluate a definition against the given (already audio-only) tracks → the ordered member list.</summary>
    public static List<Track> Evaluate(SmartPlaylistDef def, IEnumerable<Track> audio)
    {
        IEnumerable<Track> hits = def.Rules.Count == 0
            ? audio
            : audio.Where(t => def.MatchAll ? def.Rules.All(r => Matches(t, r)) : def.Rules.Any(r => Matches(t, r)));
        IEnumerable<Track> ordered = SortFor(hits, def.LimitSort);
        if (def.Limit > 0) ordered = ordered.Take(def.Limit);
        return ordered.ToList();
    }

    public static bool Matches(Track t, SmartRule r)
    {
        var f = Field(r.Field);
        if (f.Type == FieldType.Text)
        {
            string hay = (f.Text!(t) ?? "").Trim();
            string needle = (r.Value ?? "").Trim();
            const StringComparison ic = StringComparison.OrdinalIgnoreCase;
            return r.Op switch
            {
                "contains"    => needle.Length == 0 || hay.Contains(needle, ic),
                "notcontains" => !(needle.Length > 0 && hay.Contains(needle, ic)),
                "is"          => string.Equals(hay, needle, ic),
                "isnot"       => !string.Equals(hay, needle, ic),
                "startswith"  => hay.StartsWith(needle, ic),
                _             => false,
            };
        }

        if (f.Type == FieldType.Days)
        {
            if (!TryNum(r.Value, out double days)) return false;
            double? ago = f.Num!(t);            // days since added/played; null = never
            return r.Op switch
            {
                "within"    => ago.HasValue && ago.Value <= days,
                "notwithin" => !ago.HasValue || ago.Value > days,
                _           => false,
            };
        }

        // Number / Rating
        if (!TryNum(r.Value, out double rhs)) return false;
        double lhs = f.Num!(t) ?? 0;
        return r.Op switch
        {
            "is"      => lhs == rhs,
            "isnot"   => lhs != rhs,
            "atleast" => lhs >= rhs,
            "atmost"  => lhs <= rhs,
            "more"    => lhs > rhs,
            "less"    => lhs < rhs,
            _         => false,
        };
    }

    private static IEnumerable<Track> SortFor(IEnumerable<Track> src, string key) => key switch
    {
        "Title"          => src.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
        "Artist"         => src.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber),
        "Album"          => src.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber),
        "MostPlayed"     => src.OrderByDescending(t => t.PlayCount),
        "LeastPlayed"    => src.OrderBy(t => t.PlayCount),
        "HighestRated"   => src.OrderByDescending(t => t.Rating).ThenByDescending(t => t.PlayCount),
        "RecentlyPlayed" => src.OrderByDescending(t => t.LastPlayed ?? DateTime.MinValue),
        "Random"         => src.OrderBy(_ => Guid.NewGuid()),
        _                => src.OrderByDescending(t => t.DateAdded ?? DateTime.MinValue),   // "Added"
    };

    /// <summary>A short human-readable description, e.g. "Match all · 2 rules · top 25 by Most played".</summary>
    public static string Describe(SmartPlaylistDef def)
    {
        string head = def.Rules.Count == 0 ? "All songs" : $"Match {(def.MatchAll ? "all" : "any")} · {def.Rules.Count} rule{(def.Rules.Count == 1 ? "" : "s")}";
        return def.Limit > 0 ? $"{head} · top {def.Limit} by {SortLabel(def.LimitSort)}" : head;
    }

    private static double? DaysAgo(DateTime? d) => d.HasValue ? Math.Max(0, (DateTime.Now - d.Value).TotalDays) : (double?)null;

    private static bool TryNum(string? s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.TryParse(s, out v);
}
