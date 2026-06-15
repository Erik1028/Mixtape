using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mixtape.App.Views;

namespace Mixtape.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
