using System.Collections.Concurrent;
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
    private VolumeSampleProvider? _norm;   // per-track volume-normalization gain (legacy single-shot path)
    private VolumeSampleProvider? _vol;
    private OutputShaper? _shaper;          // final mono-downmix + sleep-fade stage (just before the limiter/tap)
    private GaplessSampleProvider? _src;    // persistent gapless/crossfade head; non-null only in gapless mode
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 200 };
    private readonly SynchronizationContext? _sync;

    private float _volume = 1f;
    private bool _eqEnabled;
    private float[] _gains;
    private volatile bool _stoppingForLoad; // tells PlaybackStopped apart: manual close vs. natural end
    private volatile int _loadGen;          // bumped on every (re)load so a late end-callback from a replaced track is ignored
    private bool _normEnabled;     // volume normalization on?
    private bool _mono;            // downmix stereo to mono (Pro feature)
    private volatile string? _currentPath;  // path of the current track (gapless or legacy); only mutated on the UI thread
    private static readonly ConcurrentDictionary<string, float> _gainCache = new();   // computed normalization gain per file (bounded)

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? PositionTick;
    public event Action<string>? TrackSwitched;   // gapless/crossfade advanced internally to the queued track

    public bool IsOpen { get; private set; }
    public bool GaplessActive => _src is not null;

    /// <summary>Real-time spectrum of what's currently playing (fed by a tap at the end of the chain).</summary>
    public AudioVisualizer Visualizer { get; } = new();

    public AudioPlayer(float[] initialGains, bool eqEnabled)
    {
        _gains = (float[])initialGains.Clone();
        _eqEnabled = eqEnabled;
        _sync = SynchronizationContext.Current; // captured on the UI thread for marshalling end-of-track
        _tick.Tick += (_, _) => { if (IsOpen) PositionTick?.Invoke(); };
    }

    public TimeSpan Duration => _src is not null ? _src.CurrentTotalTime : (_reader?.TotalTime ?? TimeSpan.Zero);

    public TimeSpan Position
    {
        get => _src is not null ? _src.CurrentTime : (_reader?.CurrentTime ?? TimeSpan.Zero);
        set
        {
            if (!IsOpen) return;
            if (_src is not null) _src.Seek(value);
            else if (_reader is not null) { try { _reader.CurrentTime = value; } catch { } }
        }
    }

    /// <summary>0..1.</summary>
    public double Volume
    {
        get => _volume;
        set { _volume = (float)Math.Clamp(value, 0, 1); if (_vol is not null) _vol.Volume = _volume; }
    }

    public void SetEqEnabled(bool on) { _eqEnabled = on; if (_eq is not null) _eq.Enabled = on; }
    public void SetEqGains(float[] gains) { _gains = (float[])gains.Clone(); _eq?.SetGains(_gains); }

    /// <summary>Turn volume normalization on/off, re-targeting the current track's gain live where possible.</summary>
    public void SetNormalizationEnabled(bool on)
    {
        _normEnabled = on;
        if (_norm is not null && _currentPath is not null) _norm.Volume = on ? ComputeTrackGain(_currentPath) : 1f;
        // (gapless mode bakes the gain per-voice; it applies as each track is enqueued/played)
    }

    /// <summary>Update crossfade settings on the live gapless chain (no-op in legacy mode).</summary>
    public void SetCrossfade(bool on, double seconds)
    {
        if (_src is null) return;
        _src.CrossfadeEnabled = on;
        _src.FadeFrames = (long)(Math.Clamp(seconds, 1, 12) * GaplessSampleProvider.Canonical.SampleRate);
    }

    /// <summary>Downmix output to mono (live; also baked into the next track's chain).</summary>
    public void SetMono(bool on) { _mono = on; if (_shaper is not null) _shaper.Mono = on; }

    /// <summary>Set the sleep-timer fade gain 0..1 (1 = normal). The host's sleep timer ramps this to 0.</summary>
    public void SetSleepGain(float g) { if (_shaper is not null) _shaper.SleepGain = Math.Clamp(g, 0f, 1f); }

    public void Load(string path)
    {
        CloseMedia();
        try
        {
            _currentPath = path;
            _reader = new MediaFoundationReader(path);
            var sample = _reader.ToSampleProvider();
            _eq = new EqualizerSampleProvider(sample, _gains, _eqEnabled);
            _norm = new VolumeSampleProvider(_eq) { Volume = _normEnabled ? ComputeTrackGain(path) : 1f };
            _vol = new VolumeSampleProvider(_norm) { Volume = _volume };
            _out = new WaveOutEvent();
            _out.PlaybackStopped += OnPlaybackStopped;
            _shaper = new OutputShaper(_vol) { Mono = _mono };
            _out.Init(new SampleTap(new ClampSampleProvider(_shaper), Visualizer));   // mono/sleep → limiter → tap the final output for the spectrum
            IsOpen = true;
            Opened?.Invoke();
        }
        catch (Exception ex)
        {
            CloseMedia();
            Failed?.Invoke(ex.Message);
        }
    }

    /// <summary>Open the PERSISTENT gapless/crossfade chain: the device stays open across tracks, the head
    /// <see cref="GaplessSampleProvider"/> swaps in pre-decoded next tracks so playback never stops between
    /// songs (and optionally crossfades). EQ + user volume sit downstream, unchanged.</summary>
    public void StartGapless(string path, bool crossfade, double crossSeconds, bool normalize)
    {
        CloseMedia();
        try
        {
            _currentPath = path;
            _normEnabled = normalize;
            _src = new GaplessSampleProvider
            {
                CrossfadeEnabled = crossfade,
                FadeFrames = (long)(Math.Clamp(crossSeconds, 1, 12) * GaplessSampleProvider.Canonical.SampleRate),
            };
            _src.SetCurrent(path, normalize ? ComputeTrackGain(path) : 1f);
            _src.TrackSwitched += OnGaplessTrackSwitched;
            _eq = new EqualizerSampleProvider(_src, _gains, _eqEnabled);
            _vol = new VolumeSampleProvider(_eq) { Volume = _volume };
            _out = new WaveOutEvent();
            _out.PlaybackStopped += OnPlaybackStopped;
            _shaper = new OutputShaper(_vol) { Mono = _mono };
            _out.Init(new SampleTap(new ClampSampleProvider(_shaper), Visualizer));   // mono/sleep → limiter → tap the final output for the spectrum
            IsOpen = true;
            Opened?.Invoke();
            _out.Play();
            _tick.Start();
        }
        catch (Exception ex)
        {
            CloseMedia();
            Failed?.Invoke(ex.Message);
        }
    }

    /// <summary>Pre-decode the next track so the gapless head can switch to it seamlessly.</summary>
    public void EnqueueNext(string path) => _src?.EnqueueNext(path, _normEnabled ? ComputeTrackGain(path) : 1f);
    public void ClearNext() => _src?.ClearNext();

    private void OnGaplessTrackSwitched(string path)
    {
        int gen = _loadGen;   // a boundary event already in flight must not resurrect UI state after CloseMedia
        void Raise() { if (gen != _loadGen) return; _currentPath = path; TrackSwitched?.Invoke(path); }
        if (_sync is not null) _sync.Post(_ => Raise(), null); else Raise();
    }

    /// <summary>A pragmatic loudness gain: scan up to ~30 s, hit a target RMS, and cap so we never clip.
    /// Cached per file (the next track's scan happens off the critical path during EnqueueNext).</summary>
    private float ComputeTrackGain(string path)
    {
        if (_gainCache.TryGetValue(path, out var cached)) return cached;
        float gain = 1f;
        try
        {
            using var r = new MediaFoundationReader(path);
            var sp = r.ToSampleProvider();
            int chunk = Math.Max(4096, r.WaveFormat.SampleRate * r.WaveFormat.Channels);   // ~1 s
            var buf = new float[chunk];
            double sumSq = 0; long n = 0; float peak = 0; int read;
            long cap = (long)chunk * 10;   // ~10 s — a stable-enough RMS without a long UI hitch (cached per file)
            while ((read = sp.Read(buf, 0, buf.Length)) > 0 && n < cap)
            {
                for (int i = 0; i < read; i++) { float a = Math.Abs(buf[i]); sumSq += (double)buf[i] * buf[i]; if (a > peak) peak = a; }
                n += read;
            }
            if (n > 0)
            {
                double rms = Math.Sqrt(sumSq / n);
                const double targetRms = 0.18;   // ~ -15 dBFS RMS, a sensible music target
                gain = rms > 1e-6 ? (float)(targetRms / rms) : 1f;
                if (peak > 1e-6) gain = Math.Min(gain, 0.99f / peak);   // never push a peak into clipping
                gain = Math.Clamp(gain, 0.25f, 4f);
            }
        }
        catch { gain = 1f; }
        if (_gainCache.Count > 512) _gainCache.Clear();   // bound it — the cache is static / process-lifetime
        _gainCache[path] = gain;
        return gain;
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
        if (_src is not null) { _src.TrackSwitched -= OnGaplessTrackSwitched; try { _src.Dispose(); } catch { } }
        _reader = null; _eq = null; _norm = null; _vol = null; _shaper = null; _src = null;
        IsOpen = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stoppingForLoad) return; // a manual close, not a finished track
        int gen = _loadGen;
        var error = e.Exception;   // a device error must NOT be treated as a normal track end (which would auto-advance)
        // Any error-free stop that wasn't a manual close (_stoppingForLoad, handled above) is a genuine end:
        // WaveOut only stops on its own when the source is exhausted. The old byte-exact test
        // (_reader.Position >= _reader.Length - 1) failed for VBR/compressed files whose MediaFoundationReader
        // Length is an estimate — Position halted below it at true EOF, so Ended never fired and auto-advance
        // silently stalled ("sometimes the next song doesn't play"). The _loadGen guard drops stale callbacks.
        bool natural = error is null;
        // Ignore if a new track was loaded between this stop and its UI-thread delivery (stale end-callback).
        void Raise()
        {
            if (gen != _loadGen) return;
            _tick.Stop();
            if (error is not null) Failed?.Invoke(error.Message);
            else if (natural) Ended?.Invoke();
        }
        if (_sync is not null) _sync.Post(_ => Raise(), null); else Raise();
    }

    public void Dispose() { _tick.Dispose(); CloseMedia(); }
}
