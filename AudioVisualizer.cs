using NAudio.Dsp;
using NAudio.Wave;

namespace iPodCommander;

/// <summary>
/// A cheap real-time spectrum analyzer. <see cref="SampleTap"/> feeds it the most recent audio samples from
/// the playback thread (a mono mix kept in a ring); the UI thread calls <see cref="Read"/> to get log-spaced,
/// dB-scaled band magnitudes (0..1) via an FFT. One instance lives on the <see cref="AudioPlayer"/> and is
/// shared by the now-playing bar's cover bars and the mini player's spectrum strip. Thread-safe: the ring is
/// guarded by a lock; the FFT scratch is only touched on the (single) UI thread.
/// </summary>
internal sealed class AudioVisualizer
{
    private const int N = 1024, M = 10;   // FFT window = 2^M
    private readonly float[] _ring = new float[N];
    private int _w;
    private readonly object _lock = new();
    private readonly Complex[] _fft = new Complex[N];
    private readonly float[] _hann = new float[N];

    public AudioVisualizer()
    {
        for (int i = 0; i < N; i++) _hann[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1))));
    }

    /// <summary>Playback thread: append a buffer's worth of samples (a mono mix of the most recent frames).</summary>
    public void Push(float[] buffer, int offset, int count, int channels)
    {
        if (channels < 1) channels = 1;
        lock (_lock)
        {
            for (int i = 0; i + channels - 1 < count; i += channels)
            {
                float m = 0; for (int c = 0; c < channels; c++) m += buffer[offset + i + c];
                _ring[_w] = m / channels;
                _w = (_w + 1) % N;
            }
        }
    }

    /// <summary>UI thread: fill <paramref name="bands"/> with log-spaced 0..1 magnitudes. Returns false when essentially silent.</summary>
    public bool Read(float[] bands)
    {
        lock (_lock)
        {
            int idx = _w;
            for (int i = 0; i < N; i++) { _fft[i].X = _ring[(idx + i) % N] * _hann[i]; _fft[i].Y = 0; }
        }
        FastFourierTransform.FFT(true, M, _fft);

        int bins = N / 2, b = bands.Length;
        double minBin = 2, maxBin = bins - 1;
        float peak = 0;
        for (int k = 0; k < b; k++)
        {
            double f0 = minBin * Math.Pow(maxBin / minBin, (double)k / b);
            double f1 = minBin * Math.Pow(maxBin / minBin, (double)(k + 1) / b);
            int lo = (int)Math.Floor(f0), hi = Math.Max(lo + 1, (int)Math.Ceiling(f1));
            if (hi > bins) hi = bins;
            float sum = 0; int cnt = 0;
            for (int j = lo; j < hi; j++) { float mag = (float)Math.Sqrt(_fft[j].X * _fft[j].X + _fft[j].Y * _fft[j].Y); sum += mag; cnt++; }
            float avg = cnt > 0 ? sum / cnt : 0;
            float db = (float)(20 * Math.Log10(avg + 1e-7));
            float norm = Math.Clamp((db + 62f) / 56f, 0f, 1f);   // ~ -62..-6 dB → 0..1
            bands[k] = norm;
            if (norm > peak) peak = norm;
        }
        return peak > 0.02f;
    }
}

/// <summary>Final safety limiter: clamps samples to [-1, 1] so peaks that exceed full scale — from EQ boost,
/// the crossfade summing two tracks (equal-power can reach ~1.4×), or volume-normalization make-up gain —
/// don't reach the driver as overflow/wraparound (which sounds like harsh clicks/distortion). Hard clip only
/// engages on the rare over-unity peak, so normal audio is untouched.</summary>
internal sealed class ClampSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    public ClampSampleProvider(ISampleProvider src) { _src = src; }
    public WaveFormat WaveFormat => _src.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        for (int i = offset; i < offset + n; i++)
        {
            float s = buffer[i];
            if (s > 1f) buffer[i] = 1f; else if (s < -1f) buffer[i] = -1f;
        }
        return n;
    }
}

/// <summary>Shapes the final stereo output for two Pro features: a MONO downmix (average L+R into both
/// channels — for a single earbud or hard-panned old recordings) and a live SLEEP gain the sleep-timer fade
/// ramps to zero before pausing. Both are live-settable from the UI thread; the audio thread reads them each
/// buffer (a torn float read of the gain is harmless). Placed just before the limiter + spectrum tap.</summary>
internal sealed class OutputShaper : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly bool _stereo;
    public bool Mono;
    public float SleepGain = 1f;   // 0..1; the sleep-timer fade-out drives this to 0
    public OutputShaper(ISampleProvider src) { _src = src; _stereo = src.WaveFormat.Channels == 2; }
    public WaveFormat WaveFormat => _src.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        float g = SleepGain;
        bool mono = Mono && _stereo;
        if (!mono && g >= 0.999f) return n;   // nothing to do — pass through
        if (mono)
            for (int i = 0; i + 1 < n; i += 2)
            {
                float m = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f * g;
                buffer[offset + i] = m; buffer[offset + i + 1] = m;
            }
        else
            for (int i = 0; i < n; i++) buffer[offset + i] *= g;
        return n;
    }
}

/// <summary>A transparent pass-through that copies the samples it forwards into an <see cref="AudioVisualizer"/>.
/// Placed at the very end of the chain (just before WaveOut) so it captures exactly what is heard.</summary>
internal sealed class SampleTap : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly AudioVisualizer _vis;
    public SampleTap(ISampleProvider src, AudioVisualizer vis) { _src = src; _vis = vis; }
    public WaveFormat WaveFormat => _src.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        if (n > 0) _vis.Push(buffer, offset, n, WaveFormat.Channels);
        return n;
    }
}
