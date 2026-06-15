namespace iPodCommander;

/// <summary>
/// The iTunesDB mhit "mediatype" field (@0xD0) values, and helpers to classify a track as
/// audio vs video. These match libgpod's <c>Itdb_Mediatype</c> bit values; the field stores a
/// single discrete value per track (not an OR of flags) but we test by bit so a video-podcast
/// (PODCAST|MOVIE) is still recognised as playable video.
/// </summary>
internal static class MediaType
{
    public const uint Audio       = 0x0001; // ordinary song (0 is also treated as audio)
    public const uint Movie       = 0x0002; // a video / film
    public const uint Podcast     = 0x0004; // audio podcast
    public const uint Audiobook   = 0x0008;
    public const uint MusicVideo  = 0x0020; // a music video
    public const uint TVShow      = 0x0040; // a TV-show episode
    public const uint VideoPodcast = Movie | Podcast; // 0x0006
    public const uint Ringtone    = 0x4000;

    /// <summary>Any value whose bits include a visual medium the 5G/Classic shows under "Videos".</summary>
    private const uint VideoBits = Movie | MusicVideo | TVShow;

    public static bool IsVideo(uint mediaType) => (mediaType & VideoBits) != 0;

    public static bool IsAudio(uint mediaType) => !IsVideo(mediaType);

    /// <summary>The three video kinds the Add-video flow can assign.</summary>
    public enum VideoKind { Movie, MusicVideo, TVShow }

    public static uint ValueFor(VideoKind kind) => kind switch
    {
        VideoKind.MusicVideo => MusicVideo,
        VideoKind.TVShow => TVShow,
        _ => Movie,
    };
}
