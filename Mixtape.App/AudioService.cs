using LibVLCSharp.Shared;

namespace Mixtape.App;

/// <summary>
/// Cross-platform audio playback + 10-band equalizer via LibVLC (decodes every iPod format:
/// mp3/aac/m4a/alac/wav/flac/…). Replaces the Windows-only NAudio engine for the Avalonia app.
/// On Linux this needs libvlc present (e.g. `apt install vlc`); on Windows it's bundled.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly LibVLC? _vlc;
    private readonly MediaPlayer? _player;
    private bool _hasMedia;

    /// <summary>False when libvlc couldn't be initialised (e.g. VLC isn't installed on this Linux box).
    /// All playback methods become no-ops; check this and show <see cref="UnavailableReason"/> instead of crashing.</summary>
    public bool Available => _player is not null;

    /// <summary>Why audio is unavailable (the init error), or null when <see cref="Available"/>.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Raised (on a VLC thread) when time/length/state changes — marshal to the UI yourself.</summary>
    public event Action? Changed;

    public AudioService()
    {
        try
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
        catch (Exception ex)
        {
            // No libvlc (common on a bare Linux install): keep the app fully usable for
            // browsing/copying; only playback is disabled.
            UnavailableReason = ex.Message;
            try { _vlc?.Dispose(); } catch { }
            _vlc = null;
            _player = null;
        }
    }

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public long PositionMs => _player is null || _player.Time < 0 ? 0 : _player.Time;
    public long DurationMs => _player is null || _player.Length < 0 ? 0 : _player.Length;
    public int Volume { get => _player is null || _player.Volume < 0 ? 100 : _player.Volume; set { if (_player is not null) _player.Volume = Math.Clamp(value, 0, 100); } }

    public void Play(string path)
    {
        if (_player is null) return;
        using var media = new Media(_vlc!, path, FromType.FromPath);
        _player.Play(media);
        _hasMedia = true;
    }

    /// <summary>Toggle play/pause on the current media.</summary>
    public void TogglePause()
    {
        if (_player is null || !_hasMedia) return;
        _player.SetPause(_player.IsPlaying);   // pause if playing, resume if paused
    }

    public void Stop() => _player?.Stop();
    public void SeekFraction(double f) { if (_player is not null && _hasMedia) _player.Position = (float)Math.Clamp(f, 0, 1); }

    // ---- equalizer (VLC's built-in 10-band graphic EQ) ----
    public const int BandCount = 10;
    /// <summary>Approx. centre frequencies of VLC's 10 bands, for UI labels only.</summary>
    public static readonly int[] BandFrequencies = { 60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000 };

    /// <summary>Apply (or clear) the 10-band EQ. <paramref name="gainsDb"/> ±20 dB per band.</summary>
    public void SetEq(bool enabled, float[]? gainsDb)
    {
        if (_player is null) return;
        var eq = new Equalizer();   // all bands 0 = flat
        if (enabled && gainsDb is not null)
            for (uint b = 0; b < BandCount && b < gainsDb.Length; b++)
                eq.SetAmp(gainsDb[b], b);
        _player.SetEqualizer(eq);
    }

    public void Dispose()
    {
        try { _player?.Stop(); } catch { }
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
