using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace iPodCommander;

/// <summary>
/// The swappable HEAD of the audio chain that powers gapless playback + crossfade. It decodes the current
/// track and, when that track drains, transparently continues from a PRE-SUPPLIED next track WITHOUT ever
/// returning 0 (so the single <see cref="WaveOutEvent"/> downstream never stops → no gap between songs).
/// When crossfade is on it instead reads BOTH tracks during the last fade window and sums them with an
/// equal-power curve (a real overlap, one output stream — no second device, no mixer). Every source is
/// resampled to one fixed canonical format so the device format never changes mid-stream, and each voice
/// carries its own normalization gain.
///
/// Threading: <see cref="Read"/> runs on NAudio's playback thread and holds <c>_lock</c> for its whole body;
/// the mutators (SetCurrent/EnqueueNext/ClearNext/Seek/Dispose) MUST be called on the UI thread and also take
/// <c>_lock</c>, so a reader is never disposed mid-Read. The clock (CurrentTime/CurrentTotalTime) is published
/// to lock-free volatile fields so UI paints/ticks never block on the decode lock. <see cref="TrackSwitched"/>
/// is raised OUTSIDE the lock.
/// </summary>
internal sealed class GaplessSampleProvider : ISampleProvider
{
    public static readonly WaveFormat Canonical = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public WaveFormat WaveFormat => Canonical;

    private sealed class Voice
    {
        public MediaFoundationReader Reader = null!;
        public VolumeSampleProvider Gain = null!;   // per-track normalization (last stage feeding Samples)
        public ISampleProvider Samples = null!;      // reader → resample → stereo → gain, all in Canonical
        public string Path = "";
    }

    private Voice? _current;
    private Voice? _next;                 // pre-decoded, waiting at the boundary
    private readonly object _lock = new();

    private bool _crossfading;            // currently inside the overlap window
    private long _fadePos;                // frames elapsed in the current fade
    private long _fadeLen;                // effective fade length (clamped to the outgoing track's remainder)
    private float[]? _tmpA, _tmpB;        // scratch for the overlap mix
    private long _curMs, _totMs;          // lock-free clock snapshot (ms), published at the end of each Read

    /// <summary>True → blend the last <see cref="FadeFrames"/> frames of each track into the next.</summary>
    public bool CrossfadeEnabled { get; set; }
    /// <summary>Crossfade length in frames (stereo sample-pairs). 0 → instant (gapless) swap.</summary>
    public long FadeFrames { get; set; }

    /// <summary>Raised (on no particular thread — caller marshals) when playback has actually moved on to the
    /// formerly-next track, so the UI flips metadata/cover/highlight exactly once per boundary.</summary>
    public event Action<string>? TrackSwitched;

    public bool HasNext => Volatile.Read(ref _next) is not null;
    public TimeSpan CurrentTime => TimeSpan.FromMilliseconds(Interlocked.Read(ref _curMs));
    public TimeSpan CurrentTotalTime => TimeSpan.FromMilliseconds(Interlocked.Read(ref _totMs));

    public void Seek(TimeSpan t)
    {
        lock (_lock)
        {
            if (_crossfading) { DisposeVoice(_next); _next = null; }   // abandon the half-mixed incoming; prefetch re-stages it
            _crossfading = false; _fadePos = 0;
            if (_current is not null) try { _current.Reader.CurrentTime = t; } catch { }
            UpdateClock();
        }
    }

    public void SetCurrent(string path, float normGain)
    {
        var v = Build(path, normGain);   // open/decode happens OUTSIDE the lock
        lock (_lock) { DisposeVoice(_current); _current = v; DisposeVoice(_next); _next = null; _crossfading = false; _fadePos = 0; UpdateClock(); }
    }

    public void EnqueueNext(string path, float normGain)
    {
        var v = Build(path, normGain);   // decode/open happens here, ahead of the boundary (UI thread)
        lock (_lock) { DisposeVoice(_next); _next = v; }
    }

    public void ClearNext() { lock (_lock) { DisposeVoice(_next); _next = null; } }

    // Exception-safe: if any stage after the reader is constructed throws (odd/corrupt file), dispose the
    // reader so we don't leak a file handle (and the throw can propagate to a caller that handles it).
    private static Voice Build(string path, float normGain)
    {
        var reader = new MediaFoundationReader(path);
        try
        {
            ISampleProvider s = reader.ToSampleProvider();
            if (s.WaveFormat.SampleRate != Canonical.SampleRate) s = new WdlResamplingSampleProvider(s, Canonical.SampleRate);
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            var gain = new VolumeSampleProvider(s) { Volume = normGain };
            return new Voice { Reader = reader, Gain = gain, Samples = gain, Path = path };
        }
        catch { reader.Dispose(); throw; }
    }

    private static void DisposeVoice(Voice? v) { if (v is not null) try { v.Reader.Dispose(); } catch { } }

    public int Read(float[] buffer, int offset, int count)
    {
        string? switched = null;
        int read = 0;
        lock (_lock)
        {
            if (_current is null) return 0;

            // Enter the overlap when we're within a fade length of the end AND the next track is ready. Clamp the
            // effective fade to whatever the outgoing track has left, so a track shorter than the fade still ends
            // the blend at gain 0 → 1 (no volume snap when it drains early).
            if (CrossfadeEnabled && FadeFrames > 0 && !_crossfading && _next is not null && RemainingFrames(_current) <= FadeFrames)
            { _crossfading = true; _fadePos = 0; _fadeLen = Math.Max(1, Math.Min(FadeFrames, RemainingFrames(_current))); }

            if (!_crossfading)
            {
                read = _current.Samples.Read(buffer, offset, count);
                if (read == 0 && _next is not null) { switched = Promote(); read = _current!.Samples.Read(buffer, offset, count); }
                // read == 0 with no next → genuine end of queue → WaveOut stops → AudioPlayer raises Ended
            }
            else
            {
                read = MixOverlap(buffer, offset, count, ref switched);
            }
            UpdateClock();
        }
        if (switched is not null) TrackSwitched?.Invoke(switched);   // outside the lock
        return read;
    }

    // Read current + next into scratch, sum them with an equal-power fade (current down, next up). When the
    // fade completes (or current drains), promote next → current and finish the buffer from it.
    private int MixOverlap(float[] buffer, int offset, int count, ref string? switched)
    {
        if (_tmpA is null || _tmpA.Length < count) { _tmpA = new float[count]; _tmpB = new float[count]; }
        int rc = ReadFull(_current!.Samples, _tmpA, count);
        int rn = _next is not null ? ReadFull(_next.Samples, _tmpB!, count) : 0;
        int n = Math.Max(rc, rn);
        for (int f = 0; f + 1 < n; f += 2)
        {
            double t = _fadeLen > 0 ? Math.Min(1.0, _fadePos / (double)_fadeLen) : 1.0;
            float gOut = (float)Math.Cos(0.5 * Math.PI * t);
            float gIn = (float)Math.Sin(0.5 * Math.PI * t);
            float aL = f < rc ? _tmpA[f] : 0f, aR = f + 1 < rc ? _tmpA[f + 1] : 0f;
            float bL = f < rn ? _tmpB![f] : 0f, bR = f + 1 < rn ? _tmpB![f + 1] : 0f;
            buffer[offset + f] = aL * gOut + bL * gIn;
            buffer[offset + f + 1] = aR * gOut + bR * gIn;
            _fadePos++;
        }
        if (_fadePos >= _fadeLen || rc == 0)   // fade done, or the outgoing track ran out → incoming becomes current
        {
            if (_next is not null) switched = Promote();
            _crossfading = false; _fadePos = 0;
        }
        return n;
    }

    // Pull exactly count samples (or until the source is exhausted) — a single Read may return less.
    private static int ReadFull(ISampleProvider src, float[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = src.Read(buf, total, count - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    // Dispose the outgoing voice, make the queued one current. Returns the new path (raise TrackSwitched).
    private string Promote()
    {
        DisposeVoice(_current);
        _current = _next!;
        _next = null;
        return _current.Path;
    }

    private void UpdateClock()
    {
        var c = _current;
        if (c is null) return;
        try
        {
            Interlocked.Exchange(ref _curMs, (long)c.Reader.CurrentTime.TotalMilliseconds);
            Interlocked.Exchange(ref _totMs, (long)c.Reader.TotalTime.TotalMilliseconds);
        }
        catch { }
    }

    private static long RemainingFrames(Voice v)
    {
        try
        {
            double rem = (v.Reader.TotalTime - v.Reader.CurrentTime).TotalSeconds;
            return rem <= 0 ? 0 : (long)(rem * Canonical.SampleRate);
        }
        catch { return long.MaxValue; }
    }

    public void Dispose() { lock (_lock) { DisposeVoice(_current); _current = null; DisposeVoice(_next); _next = null; } }
}
