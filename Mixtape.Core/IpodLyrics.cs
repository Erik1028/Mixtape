using System.Text;

namespace iPodCommander;

/// <summary>
/// Lyrics the iPod itself can show (Classic / nano / 5G: press the centre button while a song plays).
///
/// The iPod reads them from the song FILE — an ID3 USLT frame in an MP3, the ©lyr atom in an AAC/ALAC .m4a —
/// and only for a track whose database row carries the lyrics flag (mhit 0xB0, which iTunes set whenever the
/// file had lyrics). So putting lyrics on the iPod means writing plain text into the iPod's COPY of the song
/// and flagging the row. The listener's own files on the PC are never touched.
///
/// The iPod has no timed display, so a synced sheet goes on as its words only, one line per line, with the
/// line breaks iTunes used (a lone CR).
/// </summary>
internal static class IpodLyrics
{
    /// <summary>The formats that carry a lyrics tag the iPod reads. WAV/AIFF have none, so they are skipped.</summary>
    public static bool Supports(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".m4a" or ".m4b" or ".mp4";
    }

    /// <summary>True when the file already carries lyrics in its tags (the case a copied song brings with it).</summary>
    public static bool HasEmbedded(string path)
    {
        if (!Supports(path) || !File.Exists(path)) return false;
        try
        {
            using var f = TagLib.File.Create(path);
            return !string.IsNullOrWhiteSpace(f.Tag.Lyrics);
        }
        catch { return false; }
    }

    /// <summary>The words of a sheet as the iPod shows them: no timings, a blank line kept between verses
    /// (never two in a row), CR line breaks. Null when there are no words at all.</summary>
    public static string? PlainText(IReadOnlyList<LyricLine> lines)
    {
        var sb = new StringBuilder();
        bool lastBlank = true;   // no blank line at the very top
        foreach (var l in lines)
        {
            string text = (l.Text ?? "").Trim();
            if (text.Length == 0)
            {
                if (!lastBlank) { sb.Append('\r'); lastBlank = true; }
                continue;
            }
            sb.Append(text).Append('\r');
            lastBlank = false;
        }
        string s = sb.ToString().TrimEnd('\r');
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Write <paramref name="text"/> into the file's lyrics tag. For an MP3 this is an ID3v2 USLT frame in
    /// English with no description (what iTunes wrote); a file with no ID3v2 tag yet gets a v2.3 one, the
    /// version every iPod firmware reads. Returns false when the format has no lyrics tag or the write failed.
    /// </summary>
    public static bool Embed(string path, string text)
    {
        if (!Supports(path) || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var f = TagLib.File.Create(path);
            if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                bool had = f.GetTag(TagLib.TagTypes.Id3v2, false) is not null;
                if (f.GetTag(TagLib.TagTypes.Id3v2, true) is not TagLib.Id3v2.Tag id3) return false;
                if (!had) id3.Version = 3;
                // One lyrics frame only: a second, in another language or with a description, may be the one
                // the iPod picks.
                foreach (var old in id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().ToList())
                    id3.RemoveFrame(old);
                var frame = new TagLib.Id3v2.UnsynchronisedLyricsFrame("", "eng", TagLib.StringType.UTF16) { Text = text };
                id3.AddFrame(frame);
            }
            else
            {
                f.Tag.Lyrics = text;
            }
            f.Save();
            return true;
        }
        catch { return false; }
    }
}
