using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace iPodCommander;

/// <summary>
/// Puts a name to a file that has none. It asks the same public database the lyrics come from (lrclib.net),
/// because what that database knows about a recording is exactly what is missing here: the artist, the title
/// and the album. The file's own LENGTH is what makes the answer trustworthy - several artists record a song
/// under the same title, and the one whose recording runs as long as this file is the one you have.
/// </summary>
internal static class Identify
{
    public sealed class Candidate
    {
        public string Artist = "", Title = "", Album = "";
        public double Seconds;
        public double Delta;            // how far its length is from the file's
    }

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mixtape (https://github.com/fgs8z2n9qh-tech/Mixtape)");
        return c;
    }

    /// <summary>A file name as a search phrase: no extension, no leading track number, no underscores.</summary>
    public static string NameFrom(string path)
    {
        string s = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '0')) i++;
        if (i > 0 && i <= 3 && i < s.Length)
        {
            string rest = s[i..].TrimStart();
            if (rest.StartsWith("-") || rest.StartsWith(".")) s = rest.TrimStart('-', '.', ' ');
        }
        return TagTidy.Clean(s);
    }

    /// <summary>Blocks on the network; call it off the UI thread. Never throws - an empty list means "no idea".</summary>
    public static List<Candidate> Search(string path, double fileSeconds)
    {
        string name = NameFrom(path);
        if (name.Length == 0) return new List<Candidate>();

        var found = new List<Candidate>();
        // "Artist - Title" is the other common way a bare file is named, so ask that way too.
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        Query(name, null, found);
        if (dash > 0)
        {
            Query(name[(dash + 3)..].Trim(), name[..dash].Trim(), found);
            Query(name[..dash].Trim(), name[(dash + 3)..].Trim(), found);
        }

        foreach (var c in found) c.Delta = fileSeconds > 0 ? Math.Abs(c.Seconds - fileSeconds) : 0;
        return found
            .GroupBy(c => (c.Artist.ToLowerInvariant(), c.Title.ToLowerInvariant(), c.Album.ToLowerInvariant()))
            .Select(g => g.OrderBy(c => c.Delta).First())
            .OrderBy(c => c.Delta)
            .ThenByDescending(c => c.Album.Length > 0)
            .Take(12)
            .ToList();
    }

    private static void Query(string title, string? artist, List<Candidate> into)
    {
        if (title.Length == 0) return;
        try
        {
            var q = new StringBuilder("https://lrclib.net/api/search?track_name=").Append(Uri.EscapeDataString(title));
            if (!string.IsNullOrWhiteSpace(artist)) q.Append("&artist_name=").Append(Uri.EscapeDataString(artist));
            var res = Http.GetAsync(q.ToString()).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) return;
            if (JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult()) is not JsonArray arr) return;
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                string a = Str(o, "artistName"), t = Str(o, "trackName"), al = Str(o, "albumName");
                if (a.Length == 0 || t.Length == 0) continue;
                double secs = o["duration"]?.GetValue<double>() ?? 0;
                into.Add(new Candidate { Artist = a, Title = t, Album = al, Seconds = secs });
            }
        }
        catch { /* offline, throttled, malformed - the caller shows "nothing found" */ }
    }

    private static string Str(JsonObject o, string key) => o[key]?.GetValue<string>()?.Trim() ?? "";

    /// <summary>Writes the three fields into the file itself and leaves everything else, audio included, alone.</summary>
    public static void Write(string path, Candidate c)
    {
        using var f = TagLib.File.Create(path);
        f.Tag.Title = c.Title;
        f.Tag.Performers = new[] { c.Artist };
        if (string.IsNullOrWhiteSpace(f.Tag.FirstAlbumArtist)) f.Tag.AlbumArtists = new[] { c.Artist };
        if (c.Album.Length > 0) f.Tag.Album = c.Album;
        f.Save();
    }
}
