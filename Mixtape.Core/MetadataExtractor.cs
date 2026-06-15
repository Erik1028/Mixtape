namespace iPodCommander;

/// <summary>
/// Reads the tags + audio properties a new track needs, using TagLib#. Falls back to the
/// file name for the title and never throws — a tagless file still copies and plays.
/// </summary>
internal static class MetadataExtractor
{
    public static NewTrack Read(string path, bool isVideo = false)
    {
        var fi = new FileInfo(path);
        var nt = new NewTrack
        {
            FileSize = (uint)Math.Min(fi.Length, uint.MaxValue),
            Title = Path.GetFileNameWithoutExtension(path),
        };

        string ext = Path.GetExtension(path).ToLowerInvariant();
        (nt.Type2, nt.FileTypeDescription) = isVideo
            ? ext switch
            {
                ".mov" => ((byte)0, "QuickTime movie file"),
                _ => ((byte)0, "MPEG-4 video file"),   // .m4v / .mp4
            }
            : ext switch
            {
                ".mp3" => ((byte)1, "MPEG audio file"),
                ".m4a" or ".aac" or ".mp4" or ".m4b" => ((byte)0, "AAC audio file"),
                ".wav" => ((byte)1, "WAV audio file"),
                ".aif" or ".aiff" => ((byte)1, "AIFF audio file"),
                _ => ((byte)1, "Audio file"),
            };

        try
        {
            using var f = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(f.Tag.Title)) nt.Title = f.Tag.Title;
            nt.Artist = f.Tag.FirstPerformer ?? f.Tag.JoinedPerformers;
            nt.Album = f.Tag.Album;
            nt.Genre = f.Tag.FirstGenre;
            nt.Year = f.Tag.Year;
            nt.TrackNumber = f.Tag.Track;
            nt.TotalTracks = f.Tag.TrackCount;
            double ms = f.Properties.Duration.TotalMilliseconds;
            nt.LengthMs = ms > 0 && ms < uint.MaxValue ? (uint)ms : 0;          // a bogus/negative duration must not wrap
            nt.Bitrate = (uint)Math.Clamp(f.Properties.AudioBitrate, 0, int.MaxValue);
            nt.SampleRate = (uint)Math.Clamp(f.Properties.AudioSampleRate, 0, int.MaxValue);
            // TagLib reports 0 kbps for some AAC/m4a files; estimate from size and duration so the
            // track doesn't show "0 kbps" (≈ overall average bitrate, close enough for display).
            if (nt.Bitrate == 0 && nt.LengthMs > 0 && fi.Length > 0)
                nt.Bitrate = (uint)Math.Clamp(fi.Length * 8.0 / nt.LengthMs, 0, 100_000); // bytes*8 / ms = kbps
        }
        catch
        {
            // Unreadable/odd tags — keep the file-name title and zeroed properties.
        }
        return nt;
    }

    /// <summary>The embedded cover-art bytes for an audio file, or null if there's none / unreadable.</summary>
    public static byte[]? ReadArt(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var pics = f.Tag.Pictures;
            if (pics is { Length: > 0 } && pics[0].Data?.Data is { Length: > 0 } data) return data;
        }
        catch { }
        return null;
    }
}
