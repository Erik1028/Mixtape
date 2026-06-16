using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace iPodCommander;

/// <summary>
/// Audio playback via NAudio (so we can insert an equalizer, which WPF's MediaElement can't expose):
/// MediaFoundationReader (same Windows codecs as before — MP3 / AAC-M4A / ALAC / WAV) → 10-band
/// <see cref="EqualizerSampleProvider"/> → volume → WaveOut. Mirrors the audio surface of MediaEngine
/// so the now-playing bar can use it directly. Video preview still uses MediaEngine (it needs a picture).
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private MediaFoundationReader? _reader;
    private EqualizerSampleProvider? _eq;
    private VolumeSampleProvider? _vol;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 200 };
    private readonly SynchronizationContext? _sync;

    private float _volume = 1f;
    private bool _eqEnabled;
    private float[] _gains;
    private bool _stoppingForLoad; // tells PlaybackStopped apart: manual close vs. natural end
    private int _loadGen;          // bumped on every (re)load so a late end-callback from a replaced track is ignored

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? PositionTick;

    public bool IsOpen { get; private set; }

    public AudioPlayer(float[] initialGains, bool eqEnabled)
    {
        _gains = (float[])initialGains.Clone();
        _eqEnabled = eqEnabled;
        _sync = SynchronizationContext.Current; // captured on the UI thread for marshalling end-of-track
        _tick.Tick += (_, _) => { if (IsOpen) PositionTick?.Invoke(); };
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (_reader is not null && IsOpen) { try { _reader.CurrentTime = value; } catch { } } }
    }

    /// <summary>0..1.</summary>
    public double Volume
    {
        get => _volume;
        set { _volume = (float)Math.Clamp(value, 0, 1); if (_vol is not null) _vol.Volume = _volume; }
    }

    public void SetEqEnabled(bool on) { _eqEnabled = on; if (_eq is not null) _eq.Enabled = on; }
    public void SetEqGains(float[] gains) { _gains = (float[])gains.Clone(); _eq?.SetGains(_gains); }

    public void Load(string path)
    {
        CloseMedia();
        try
        {
            _reader = new MediaFoundationReader(path);
            var sample = _reader.ToSampleProvider();
            _eq = new EqualizerSampleProvider(sample, _gains, _eqEnabled);
            _vol = new VolumeSampleProvider(_eq) { Volume = _volume };
            _out = new WaveOutEvent();
            _out.PlaybackStopped += OnPlaybackStopped;
            _out.Init(_vol);
            IsOpen = true;
            Opened?.Invoke();
        }
        catch (Exception ex)
        {
            CloseMedia();
            Failed?.Invoke(ex.Message);
        }
    }

    public void Play() { if (_out is null) return; _out.Play(); _tick.Start(); }
    public void Pause() => _out?.Pause();

    /// <summary>Stop and fully release the current media (so the file isn't held open).</summary>
    public void CloseMedia()
    {
        _loadGen++;       // invalidate any end-callback still in flight for the track we're closing
        _tick.Stop();
        if (_out is not null)
        {
            _out.PlaybackStopped -= OnPlaybackStopped;
            _stoppingForLoad = true;
            try { _out.Stop(); } catch { }
            try { _out.Dispose(); } catch { }
            _out = null;
            _stoppingForLoad = false;
        }
        try { _reader?.Dispose(); } catch { }
        _reader = null; _eq = null; _vol = null;
        IsOpen = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stoppingForLoad) return; // a manual close, not a finished track
        int gen = _loadGen;
        bool natural = _reader is not null && _reader.Position >= _reader.Length - 1;
        // Ignore if a new track was loaded between this stop and its UI-thread delivery (stale end-callback).
        void Raise() { if (gen != _loadGen) return; _tick.Stop(); if (natural) Ended?.Invoke(); }
        if (_sync is not null) _sync.Post(_ => Raise(), null); else Raise();
    }

    public void Dispose() { _tick.Dispose(); CloseMedia(); }
}
