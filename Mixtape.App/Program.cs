using Avalonia;

namespace Mixtape.App;

internal static class Program
{
    // Avalonia entry point. Cross-platform: UsePlatformDetect picks Win32 / X11 / macOS at runtime.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless audio engine self-test (no GUI): `Mixtape.App --audiotest <file>`.
        if (args.Length >= 2 && args[0] == "--audiotest") { AudioSelfTest(args[1]); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void AudioSelfTest(string file)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            using var audio = new AudioService();
            audio.Play(file);
            long dur = 0;
            for (int i = 0; i < 25 && dur == 0; i++) { Thread.Sleep(150); dur = audio.DurationMs; }
            long p1 = audio.PositionMs;
            Thread.Sleep(1500);
            long p2 = audio.PositionMs;
            audio.SetEq(true, new float[] { 6, 5, 4, 2, 0, 0, 2, 4, 5, 6 });
            Thread.Sleep(200);
            bool ok = audio.IsPlaying && p2 > p1;
            log.AppendLine($"file       : {file}");
            log.AppendLine($"isPlaying  : {audio.IsPlaying}");
            log.AppendLine($"duration ms: {dur}");
            log.AppendLine($"pos1 ms    : {p1}");
            log.AppendLine($"pos2 ms    : {p2}  (after ~1.5s)");
            log.AppendLine($"advanced   : {p2 > p1}");
            log.AppendLine($"eq bands   : {AudioService.BandCount}");
            log.AppendLine($"RESULT: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); }
        File.WriteAllText("audiotest.txt", log.ToString());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
