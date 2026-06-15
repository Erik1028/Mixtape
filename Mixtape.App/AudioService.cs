using LibVLCSharp.Shared;

namespace Mixtape.App;

/// <summary>
/// Cross-platform audio playback + 10-band equalizer via LibVLC (decodes every iPod format:
/// mp3/aac/m4a/alac/wav/flac/…). Replaces the Windows-only NAudio engine for the Avalonia app.
/// On Linux this needs libvlc present (e.g. `apt install vlc`); on Windows it's bundled.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private bool _hasMedia;

    /// <summary>Raised (on a VLC thread) when time/length/state changes — marshal to the UI yourself.</summary>
    public event Action? Changed;

    public AudioService()
    {
        Core.Initialize();
        _vlc = new LibVLC("--no-video", "--quiet");
        _player = new MediaPlayer(_vlc);
        _player.TimeChanged += (_, _) => Changed?.Invoke();
        _player.LengthChanged += (_, _) => Changed?.Invoke();
        _player.Playing += (_, _) => Changed?.Invoke();
        _player.Paused += (_, _) => Changed?.Invoke();
        _player.Stopped += (_, _) => Changed?.Invoke();
        _player.EndReached += (_, _) => Changed?.Invoke();
    }

    public bool IsPlaying => _player.IsPlaying;
    public long PositionMs => _player.Time < 0 ? 0 : _player.Time;
    public long DurationMs => _player.Length < 0 ? 0 : _player.Length;
    public int Volume { get => _player.Volume < 0 ? 100 : _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 100); }

    public void Play(string path)
    {
        using var media = new Media(_vlc, path, FromType.FromPath);
        _player.Play(media);
        _hasMedia = true;
    }

    /// <summary>Toggle play/pause on the current media.</summary>
    public void TogglePause()
    {
        if (!_hasMedia) return;
        _player.SetPause(_player.IsPlaying);   // pause if playing, resume if paused
    }

    public void Stop() => _player.Stop();
    public void SeekFraction(double f) { if (_hasMedia) _player.Position = (float)Math.Clamp(f, 0, 1); }

    // ---- equalizer (VLC's built-in 10-band graphic EQ) ----
    public const int BandCount = 10;
    /// <summary>Approx. centre frequencies of VLC's 10 bands, for UI labels only.</summary>
    public static readonly int[] BandFrequencies = { 60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000 };

    /// <summary>Apply (or clear) the 10-band EQ. <paramref name="gainsDb"/> ±20 dB per band.</summary>
    public void SetEq(bool enabled, float[]? gainsDb)
    {
        var eq = new Equalizer();   // all bands 0 = flat
        if (enabled && gainsDb is not null)
            for (uint b = 0; b < BandCount && b < gainsDb.Length; b++)
                eq.SetAmp(gainsDb[b], b);
        _player.SetEqualizer(eq);
    }

    public void Dispose()
    {
        try { _player.Stop(); } catch { }
        _player.Dispose();
        _vlc.Dispose();
    }
}
