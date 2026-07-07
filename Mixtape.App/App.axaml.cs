using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mixtape.App.Views;

namespace Mixtape.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The shared, live-retinted brush for a theme resource key (mutated in place by AppTheme.Apply,
    /// so assigning the instance keeps following accent/variant changes).</summary>
    public static Avalonia.Media.IBrush Brush(string key)
        => Current?.Resources[key] as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Test aid: `--settings` opens the Settings dialog directly (for screenshots).
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--settings") >= 0)
            {
                AppTheme.Apply("Teal", "Graphite");
                desktop.MainWindow = new Views.SettingsWindow();
            }
            else desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
