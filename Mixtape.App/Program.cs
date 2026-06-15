using Avalonia;

namespace Mixtape.App;

internal static class Program
{
    // Avalonia entry point. Cross-platform: UsePlatformDetect picks Win32 / X11 / macOS at runtime.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
