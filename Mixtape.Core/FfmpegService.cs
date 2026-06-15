using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace iPodCommander;

/// <summary>A transcode target the iPod firmware will accept.</summary>
internal sealed record VideoTarget(int MaxWidth, int MaxHeight, int VideoKbps, int MaxrateKbps, int AudioKbps, string Label)
{
    // The original 2005 5G is the strict ceiling: H.264 Baseline Low-Complexity (single ref frame,
    // no B-frames, no CABAC), ≤320x240, ≤768 kbps, AAC-LC ≤160 kbps. This file plays on the 5G,
    // 5.5G and every Classic, so it's the safe default. (Apple 5G spec + ffmpeg-devel constraints.)
    public static VideoTarget Safe => new(320, 240, 700, 768, 128, "iPod-safe 320×240");
    // The 5.5G and every Classic decode 640x480 Baseline L3.0 up to 1.5 Mbps (NOT the original 5G).
    public static VideoTarget High => new(640, 480, 1400, 1500, 160, "High 640×480 (Classic / 5.5G)");

    public static VideoTarget ForQuality(string? q) =>
        string.Equals(q, "High", StringComparison.OrdinalIgnoreCase) ? High : Safe;
}

/// <summary>What ffprobe / ffmpeg -i told us about a source video.</summary>
internal sealed class VideoProbe
{
    public double DurationSec;
    public int Width, Height;
    public string? VideoCodec, VideoProfile, AudioCodec;
}

/// <summary>
/// Wraps a local ffmpeg/ffprobe install to (a) probe a source video, (b) decide whether it is
/// already iPod-ready, and (c) transcode anything else to a device-safe H.264/AAC .m4v with
/// progress + cancellation. ffmpeg is optional — <see cref="Detect"/> returns null if it is not
/// installed, and the caller falls back to a copy-as-is path with a warning.
/// </summary>
internal sealed class FfmpegService
{
    public string FfmpegPath { get; }
    public string? FfprobePath { get; }

    private FfmpegService(string ffmpeg, string? ffprobe) { FfmpegPath = ffmpeg; FfprobePath = ffprobe; }

    /// <summary>Locate ffmpeg: an explicit setting, then the app folder, then anywhere on PATH.</summary>
    public static FfmpegService? Detect(string? manualPath)
    {
        string? ff = ResolveExe(manualPath, "ffmpeg.exe");
        if (ff is null) return null;
        // ffprobe usually sits next to ffmpeg; otherwise look on PATH, else null (we can fall back to -i).
        string sibling = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        string? probe = File.Exists(sibling) ? sibling : ResolveExe(null, "ffprobe.exe");
        return new FfmpegService(ff, probe);
    }

    private static string? ResolveExe(string? manual, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(manual))
        {
            if (File.Exists(manual)) return manual;
            string inDir = Path.Combine(manual, fileName);
            if (File.Exists(inDir)) return inDir;
        }
        string local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { string p = Path.Combine(dir.Trim(), fileName); if (File.Exists(p)) return p; }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Probe a source video. Never throws; returns null only if both ffprobe and ffmpeg -i fail.</summary>
    public VideoProbe? Probe(string path)
    {
        if (FfprobePath is not null)
        {
            try
            {
                string args = $"-v error -select_streams v:0 -show_entries stream=width,height,codec_name,profile " +
                              $"-show_entries format=duration -of default=noprint_wrappers=1 \"{path}\"";
                var (outp, _) = Run(FfprobePath, args, null, null);
                var p = new VideoProbe();
                foreach (var line in outp.Split('\n'))
                {
                    var kv = line.Split('=', 2);
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    switch (k)
                    {
                        case "width": int.TryParse(v, out p.Width); break;
                        case "height": int.TryParse(v, out p.Height); break;
                        case "codec_name": p.VideoCodec = v; break;
                        case "profile": p.VideoProfile = v; break;
                        case "duration": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out p.DurationSec); break;
                    }
                }
                // audio codec needs a second tiny query
                var (ao, _) = Run(FfprobePath, $"-v error -select_streams a:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 \"{path}\"", null, null);
                p.AudioCodec = ao.Trim().Split('\n').FirstOrDefault()?.Trim();
                if (p.Width > 0 || p.DurationSec > 0) return p;
            }
            catch { /* fall through to ffmpeg -i */ }
        }
        return ProbeViaFfmpeg(path);
    }

    private VideoProbe? ProbeViaFfmpeg(string path)
    {
        try
        {
            var (_, err) = Run(FfmpegPath, $"-i \"{path}\"", null, null); // ffmpeg prints stream info to stderr and exits non-zero
            var p = new VideoProbe();
            var dur = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (dur.Success)
                p.DurationSec = int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture);
            var vid = Regex.Match(err, @"Stream #\d+:\d+.*?: Video:\s*(\w+)[^,]*(?:\(([^)]*)\))?.*?(\d{2,5})x(\d{2,5})");
            if (vid.Success)
            {
                p.VideoCodec = vid.Groups[1].Value;
                int.TryParse(vid.Groups[3].Value, out p.Width);
                int.TryParse(vid.Groups[4].Value, out p.Height);
            }
            var aud = Regex.Match(err, @"Stream #\d+:\d+.*?: Audio:\s*(\w+)");
            if (aud.Success) p.AudioCodec = aud.Groups[1].Value;
            return (p.Width > 0 || p.DurationSec > 0) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// True only if we can POSITIVELY confirm the source already meets the target — H.264
    /// Baseline/Constrained-Baseline, AAC audio, fits the resolution. Anything we can't identify
    /// (null profile/codec from a failed probe) is treated as NOT ready, so we transcode to be safe
    /// rather than copy a file the 5G might refuse to play.
    /// </summary>
    public static bool IsIpodReady(VideoProbe p, VideoTarget t)
    {
        bool h264 = string.Equals(p.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase);
        bool baseline = p.VideoProfile is not null &&
            (p.VideoProfile.Contains("Baseline", StringComparison.OrdinalIgnoreCase) || p.VideoProfile.Contains("Constrained", StringComparison.OrdinalIgnoreCase));
        bool aac = p.AudioCodec is not null && p.AudioCodec.Contains("aac", StringComparison.OrdinalIgnoreCase);
        bool fits = p.Width > 0 && p.Width <= t.MaxWidth && p.Height > 0 && p.Height <= t.MaxHeight;
        return h264 && baseline && aac && fits;
    }

    /// <summary>
    /// Transcode <paramref name="src"/> → <paramref name="dst"/> (.m4v) for the iPod. Reports a
    /// 0..1 fraction and can be cancelled (the ffmpeg process is killed). Throws on failure.
    /// </summary>
    public void Transcode(string src, string dst, VideoTarget t, double durationSec, Action<double> progress, Func<bool> cancelled)
    {
        // H.264 Baseline Low-Complexity: single reference frame, no B-frames, no CABAC — exactly what
        // the 2005 5G decoder requires. Scale to fit the target keeping aspect, then letterbox/pad to
        // the exact slot with black and force 1:1 SAR; AAC-LC stereo 48 kHz; faststart (moov at front).
        string vf = $"scale='min({t.MaxWidth},iw)':'min({t.MaxHeight},ih)':force_original_aspect_ratio=decrease," +
                    $"pad={t.MaxWidth}:{t.MaxHeight}:(ow-iw)/2:(oh-ih)/2:black,setsar=1";
        string args =
            $"-y -i \"{src}\" -c:v libx264 -profile:v baseline -level 3.0 " +
            $"-x264-params ref=1:bframes=0:cabac=0:weightp=0:8x8dct=0 -pix_fmt yuv420p " +
            $"-vf \"{vf}\" -r 30 -b:v {t.VideoKbps}k -maxrate {t.MaxrateKbps}k -bufsize {t.MaxrateKbps * 2}k " +
            $"-c:a aac -profile:a aac_low -b:a {t.AudioKbps}k -ar 48000 -ac 2 -movflags +faststart -f mp4 \"{dst}\"";
        RunEncode(args, dst, durationSec, progress, cancelled);
    }

    /// <summary>
    /// Transcode any audio source (FLAC/OGG/Opus/WMA/…) → an iPod-compatible AAC-LC .m4a, preserving the
    /// source tags (ffmpeg copies metadata by default). Progress 0..1; cancellable; throws on failure.
    /// </summary>
    public void TranscodeAudio(string src, string dst, int kbps, double durationSec, Action<double> progress, Func<bool> cancelled)
    {
        // -vn drops any embedded cover-art "video" stream; the `ipod` muxer writes an Apple-friendly .m4a.
        string args = $"-y -i \"{src}\" -vn -c:a aac -profile:a aac_low -b:a {kbps}k -ar 44100 -movflags +faststart -f ipod \"{dst}\"";
        RunEncode(args, dst, durationSec, progress, cancelled);
    }

    /// <summary>Run an ffmpeg encode with time-based progress + cancellation; deletes a partial output on failure/cancel.</summary>
    private void RunEncode(string args, string dst, double durationSec, Action<double> progress, Func<bool> cancelled)
    {
        var psi = new ProcessStartInfo(FfmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        var errTail = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (errTail.Length < 8000) errTail.AppendLine(e.Data);
            var m = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+\.\d+)");
            if (m.Success && durationSec > 0)
            {
                double t2 = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                progress(Math.Clamp(t2 / durationSec, 0, 1));
            }
        };
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        while (!proc.WaitForExit(200))
        {
            if (cancelled())
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                throw new OperationCanceledException();
            }
        }
        if (proc.ExitCode != 0)
        {
            try { if (File.Exists(dst)) File.Delete(dst); } catch { } // don't leave a partial/corrupt file behind
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}).\n" + Tail(errTail.ToString(), 6));
        }
    }

    private static (string Out, string Err) Run(string exe, string args, string? stdin, int? timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        // Drain stdout AND stderr concurrently — `ffmpeg -i` writes all its stream info to stderr, so
        // reading stdout to end first would deadlock once the stderr pipe buffer fills. Kill on timeout.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs ?? 60_000)) { try { p.Kill(entireProcessTree: true); } catch { } }
        try { Task.WaitAll(new Task[] { outTask, errTask }, 5000); } catch { }
        return (outTask.IsCompletedSuccessfully ? outTask.Result : "", errTask.IsCompletedSuccessfully ? errTask.Result : "");
    }

    private static string Tail(string s, int lines)
    {
        var arr = s.TrimEnd().Split('\n');
        return string.Join("\n", arr.Skip(Math.Max(0, arr.Length - lines)));
    }
}
