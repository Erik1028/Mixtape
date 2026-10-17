using System.Globalization;

namespace iPodCommander;

/// <summary>What a song is made of — "FLAC  ·  1 016 kbps  ·  44,1 kHz" — for the lyrics view's chip and the info sheet.
/// The codec comes from the file's extension (the iPod path carries one too), the iTunesDB description settling
/// the .m4a ambiguity (AAC vs Apple Lossless).</summary>
internal static class TrackFormat
{
    public static string Codec(Track t)
    {
        string d = t.FileTypeDescription ?? "";
        if (d.Contains("Lossless", StringComparison.OrdinalIgnoreCase)) return "ALAC";
        string ext;
        try { ext = Path.GetExtension(t.LocalPath ?? t.Location ?? "").ToLowerInvariant(); } catch { ext = ""; }
        switch (ext)
        {
            case ".flac": return "FLAC";
            case ".mp3": return "MP3";
            // .m4a holds AAC or Apple Lossless, and the description is not to be trusted: Mixtape itself labelled every
            // .m4a "AAC audio file" until 0.21 (MetadataExtractor), Apple Lossless included. No AAC encoder goes past
            // 320 kbps in practice, while lossless runs ~600-1100, so a fat .m4a is Apple Lossless. Many tracks carry no
            // bitrate at all (0), so it is worked out from the size and the length then.
            case ".m4a": case ".m4b":
                double kbps = t.Bitrate > 0 ? t.Bitrate : t.LengthMs > 0 ? t.FileSize * 8.0 / t.LengthMs : 0;
                return kbps >= 500 ? "ALAC" : "AAC";
            case ".aac": case ".mp4": return "AAC";
            case ".wav": return "WAV";
            case ".aif": case ".aiff": return "AIFF";
            case ".ogg": case ".oga": return "OGG";
            case ".opus": return "Opus";
            case ".wma": return "WMA";
            case ".ape": return "APE";
            case ".wv": return "WavPack";
            case ".m4v": case ".mov": return "";
        }
        if (d.Contains("MPEG audio", StringComparison.OrdinalIgnoreCase)) return "MP3";
        if (d.Contains("AAC", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (d.Contains("WAV", StringComparison.OrdinalIgnoreCase)) return "WAV";
        if (d.Contains("AIFF", StringComparison.OrdinalIgnoreCase)) return "AIFF";
        return "";
    }

    /// <summary>Codec, bitrate, sample rate (and the file size for the info sheet), in the UI's own number format.</summary>
    public static string Line(Track t, bool withSize)
    {
        var ci = Loc.Lang == "hu" ? new CultureInfo("hu-HU") : CultureInfo.InvariantCulture;
        var parts = new List<string>();
        string c = Codec(t);
        if (c.Length > 0) parts.Add(c);
        if (t.Bitrate > 0) parts.Add(t.Bitrate.ToString("N0", ci) + " kbps");
        if (t.SampleRate > 0) parts.Add((t.SampleRate / 1000.0).ToString("0.#", ci) + " kHz");
        if (withSize && t.FileSize > 0)
            parts.Add(t.FileSize >= 1048576 ? (t.FileSize / 1048576.0).ToString("0.#", ci) + " MB" : (t.FileSize / 1024.0).ToString("0.#", ci) + " KB");
        return string.Join("  ·  ", parts);
    }
}
