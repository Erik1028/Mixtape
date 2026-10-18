namespace iPodCommander;

/// <summary>
/// Harness (MIX_STARTUP=&lt;file&gt;): the launch as a timeline - every step's time since the PROCESS was created (so the
/// runtime's own start-up counts too), the thread it ran on, and how much the JIT had compiled by then. Written once the
/// window is ready and idle; MIX_STARTUP_EXIT=1 then closes it, so a bench can launch it again and again.
/// </summary>
internal static class StartupClock
{
    public static readonly string? LogPath = Environment.GetEnvironmentVariable("MIX_STARTUP") is { Length: > 0 } p ? p : null;
    public static bool On => LogPath is not null;
    public static int UiThread;

    private static readonly DateTime Created = LogPath is null ? default : System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly System.Text.StringBuilder Lines = new();
    private static double _last;

    public static void Mark(string what)
    {
        if (LogPath is null) return;
        double t = (DateTime.UtcNow - Created).TotalMilliseconds;
        double jit = System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds;
        long methods = System.Runtime.JitInfo.GetCompiledMethodCount(false);
        string th = Environment.CurrentManagedThreadId == UiThread ? "ui" : "bg";
        lock (Lines)
        {
            Lines.AppendLine($"{t,7:0} ms (+{t - _last,5:0})  {th}  jit {jit,5:0} ms/{methods,5}  {what}");
            _last = t;
        }
    }

    public static void Write()
    {
        if (LogPath is null) return;
        lock (Lines) { try { File.WriteAllText(LogPath, Lines.ToString()); } catch { } }
    }
}
