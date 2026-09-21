using System.Globalization;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Finds the tags a library has drifted on: stray spaces, and the same artist or album written several ways.
/// It only ever PROPOSES - every group carries the tracks it would touch and the exact value it would write, so
/// the dialog can show it and the user can tick what actually happens.
/// </summary>
internal static class TagTidy
{
    public enum Field { Title, Artist, Album, AlbumArtist, Genre }

    public sealed class Group
    {
        public Field What;
        public string From = "", To = "";
        public readonly List<Track> Tracks = new();
        public bool Spacing;          // a whitespace clean-up rather than a spelling merge
    }

    /// <summary>Trimmed, with inner runs of whitespace collapsed to one space.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The key two spellings of the same name share: case, accents and punctuation removed.</summary>
    private static string Key(string s)
    {
        string n = Clean(s).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (char c in n)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
        }
        return Clean(sb.ToString());
    }

    private static string Get(Track t, Field f) => f switch
    {
        Field.Title => t.Title ?? "",
        Field.Artist => t.Artist ?? "",
        Field.Album => t.Album ?? "",
        Field.AlbumArtist => t.AlbumArtist ?? "",
        _ => t.Genre ?? "",
    };

    /// <summary>What could be tidied, most tracks first. Nothing here is applied.</summary>
    public static List<Group> Scan(IReadOnlyList<Track> tracks)
    {
        var found = new List<Group>();
        var fields = new[] { Field.Title, Field.Artist, Field.Album, Field.AlbumArtist, Field.Genre };

        // 1) stray whitespace, per field and per exact value
        foreach (var f in fields)
        {
            var byValue = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (var t in tracks)
            {
                string raw = Get(t, f);
                if (raw.Length == 0) continue;
                string clean = Clean(raw);
                if (clean == raw || clean.Length == 0) continue;
                if (!byValue.TryGetValue(raw, out var gr))
                    byValue[raw] = gr = new Group { What = f, From = raw, To = clean, Spacing = true };
                gr.Tracks.Add(t);
            }
            found.AddRange(byValue.Values);
        }

        // 2) the same name written several ways (artist and album only: a title is allowed to repeat a word)
        foreach (var f in new[] { Field.Artist, Field.Album, Field.AlbumArtist })
        {
            var clusters = new Dictionary<string, Dictionary<string, List<Track>>>(StringComparer.Ordinal);
            foreach (var t in tracks)
            {
                string raw = Clean(Get(t, f));
                if (raw.Length == 0) continue;
                string k = Key(raw);
                if (k.Length == 0) continue;
                if (!clusters.TryGetValue(k, out var spellings)) clusters[k] = spellings = new Dictionary<string, List<Track>>(StringComparer.Ordinal);
                if (!spellings.TryGetValue(raw, out var list)) spellings[raw] = list = new List<Track>();
                list.Add(t);
            }
            foreach (var spellings in clusters.Values)
            {
                if (spellings.Count < 2) continue;
                // The winner is the spelling most songs already use; ties go to the one with the most capitals
                // and accents, since that is the written-out form ("Ákos" over "akos").
                var best = spellings.OrderByDescending(kv => kv.Value.Count)
                                    .ThenByDescending(kv => kv.Key.Count(char.IsUpper) + kv.Key.Count(c => c > 127))
                                    .ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
                foreach (var kv in spellings)
                {
                    if (kv.Key == best.Key) continue;
                    var g = new Group { What = f, From = kv.Key, To = best.Key };
                    g.Tracks.AddRange(kv.Value);
                    found.Add(g);
                }
            }
        }
        return found.OrderByDescending(g => g.Tracks.Count).ThenBy(g => g.From, StringComparer.CurrentCulture).ToList();
    }

    public static string Label(Field f) => f switch
    {
        Field.Title => Loc.T("Title"),
        Field.Artist => Loc.T("Artist"),
        Field.Album => Loc.T("Album"),
        Field.AlbumArtist => Loc.T("Album artist"),
        _ => Loc.T("Genre"),
    };

    /// <summary>The edit one group asks for, ready for the library writer.</summary>
    public static TrackEdit EditFor(Group g) => g.What switch
    {
        Field.Title => new TrackEdit { Title = g.To },
        Field.Artist => new TrackEdit { Artist = g.To },
        Field.Album => new TrackEdit { Album = g.To },
        Field.AlbumArtist => new TrackEdit { AlbumArtist = g.To },
        _ => new TrackEdit { Genre = g.To },
    };
}
