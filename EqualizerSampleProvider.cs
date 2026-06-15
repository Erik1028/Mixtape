using NAudio.Dsp;
using NAudio.Wave;

namespace iPodCommander;

/// <summary>
/// A 10-band graphic equalizer as an NAudio sample provider: one peaking BiQuad filter per band per
/// channel. Band gains (dB) update live without rebuilding the chain; when <see cref="Enabled"/> is
/// false the audio passes straight through. ISO-ish octave centres from 31 Hz to 16 kHz.
/// </summary>
internal sealed class EqualizerSampleProvider : ISampleProvider
{
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public const int BandCount = 10;
    private const float Q = 1.4f; // band width for a smooth 10-band graphic EQ

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[,] _filters; // [channel, band]
    private readonly float[] _gainsDb = new float[BandCount];

    public bool Enabled { get; set; }
    public WaveFormat WaveFormat => _source.WaveFormat;

    public static float[] FlatGains() => new float[BandCount];

    public EqualizerSampleProvider(ISampleProvider source, float[] gainsDb, bool enabled)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        Enabled = enabled;
        for (int b = 0; b < BandCount; b++) _gainsDb[b] = b < gainsDb.Length ? gainsDb[b] : 0f;
        int sr = source.WaveFormat.SampleRate;
        _filters = new BiQuadFilter[_channels, BandCount];
        for (int ch = 0; ch < _channels; ch++)
            for (int b = 0; b < BandCount; b++)
                _filters[ch, b] = BiQuadFilter.PeakingEQ(sr, Frequencies[b], Q, _gainsDb[b]);
    }

    /// <summary>Update one band's gain (dB) in place (coefficients only — keeps filter state, no click).</summary>
    public void SetGain(int band, float db)
    {
        if (band < 0 || band >= BandCount) return;
        _gainsDb[band] = db;
        int sr = _source.WaveFormat.SampleRate;
        for (int ch = 0; ch < _channels; ch++)
            _filters[ch, band].SetPeakingEq(sr, Frequencies[band], Q, db);
    }

    public void SetGains(float[] db)
    {
        for (int b = 0; b < BandCount; b++) SetGain(b, b < db.Length ? db[b] : 0f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled) return read;
        for (int i = 0; i < read; i++)
        {
            int ch = i % _channels;
            float s = buffer[offset + i];
            for (int b = 0; b < BandCount; b++) s = _filters[ch, b].Transform(s);
            buffer[offset + i] = s;
        }
        return read;
    }
}
