using Avalonia;
using iPodCommander;

namespace Mixtape.App;

internal static class Program
{
    // Avalonia entry point. Cross-platform: UsePlatformDetect picks Win32 / X11 / macOS at runtime.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless self-tests (no GUI).
        if (args.Length >= 2 && args[0] == "--audiotest") { AudioSelfTest(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--makesandbox") { MakeSandbox(args[1]); return; }
        if (args.Length >= 3 && args[0] == "--addtest") { AddSelfTest(args[1], args[2]); return; }

        // Single instance: two copies racing the same iPod DB swap or the shared settings.json
        // corrupt each other. Named mutexes are cross-process on Windows and Linux. (Skip in tests above.)
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, @"Global\MixtapeApp.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        GC.KeepAlive(mutex);
    }

    // Build a throwaway sandbox iPod (synthetic DB) so the copy-to-iPod path can be tested without a real device.
    private static void MakeSandbox(string dir)
    {
        var control = Path.Combine(dir, "iPod_Control");
        Directory.CreateDirectory(Path.Combine(control, "iTunes"));
        Directory.CreateDirectory(Path.Combine(control, "Device"));
        for (int i = 0; i < 50; i++) Directory.CreateDirectory(Path.Combine(control, "Music", $"F{i:00}"));
        File.WriteAllBytes(Path.Combine(control, "iTunes", "iTunesDB"), SyntheticDb.Build());
        File.WriteAllText(Path.Combine(control, "Device", "SysInfo"), "ModelNumStr: M9807\n");
        File.WriteAllText("sandbox.txt", "created sandbox iPod at " + dir);
    }

    // Verify copy-to-iPod end-to-end against a sandbox: load, add a file, save, reload, confirm track count grew.
    private static void AddSelfTest(string root, string file)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var dev = DeviceDetector.Build(root);
            if (dev is null) { log.AppendLine("no iPod at " + root); }
            else
            {
                log.AppendLine($"writable: {dev.Profile.CanWrite}");
                var lib = IpodLibrary.Load(dev);
                int before = lib.View.Tracks.Count;
                string title = lib.AddFile(file);
                lib.Save();
                var reloaded = IpodLibrary.Load(dev);
                int after = reloaded.View.Tracks.Count;
                bool present = reloaded.View.Tracks.Any(t => t.DisplayTitle == title);
                log.AppendLine($"added title : {title}");
                log.AppendLine($"before count: {before}");
                log.AppendLine($"after count : {after}");
                log.AppendLine($"present after reload: {present}");
                log.AppendLine($"RESULT: {(after == before + 1 && present ? "OK" : "FAIL")}");
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); }
        File.WriteAllText("addtest.txt", log.ToString());
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
