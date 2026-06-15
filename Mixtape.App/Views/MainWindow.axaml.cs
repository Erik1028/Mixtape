using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mixtape.App.ViewModels;

namespace Mixtape.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Apply the saved accent/theme (shared with the Windows app) and repaint the wallpaper on change.
        AppTheme.Applied += ApplyWallpaper;
        var (accent, variant) = AppConfig.Load();
        AppTheme.Apply(accent, variant);

        // Test aid: `--theme <accent> <variant>` previews a theme without saving (for screenshots).
        var cli = System.Environment.GetCommandLineArgs();
        int ti = System.Array.IndexOf(cli, "--theme");
        if (ti >= 0 && ti + 2 < cli.Length) AppTheme.Apply(cli[ti + 1], cli[ti + 2]);

        // Test aid: `--autoplay` plays the first track shortly after launch (for headless screenshotting).
        if (Environment.GetCommandLineArgs().Contains("--autoplay"))
        {
            var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); if (_vm.Tracks.Count > 0) { SongGrid.SelectedItem = _vm.Tracks[0]; _vm.PlayRow(_vm.Tracks[0]); } };
            t.Start();
        }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder of music on your PC",
            AllowMultiple = true,
        });
        if (picked.Count == 0) return;
        var paths = picked.Select(p => p.TryGetLocalPath())
                          .Where(p => !string.IsNullOrEmpty(p))
                          .Cast<string>()
                          .ToList();
        if (paths.Count > 0) _vm.AddLocalFolders(paths);
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => _vm.Refresh();

    private async void OnAddMusic(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select music to copy onto the iPod",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.m4a", "*.aac", "*.wav", "*.aif", "*.aiff", "*.m4b" } },
            },
        });
        if (files.Count == 0) return;
        var paths = files.Select(f => f.TryGetLocalPath())
                         .Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToArray();
        if (paths.Length > 0) _vm.AddMusicToIpod(paths);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        var rows = SongGrid.SelectedItems.OfType<TrackRow>().ToList();
        if (rows.Count > 0) _vm.DeleteSelected(rows);
    }

    private void OnSongDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        => _vm.PlayRow(SongGrid.SelectedItem as TrackRow);

    private void OnPlayPause(object? sender, RoutedEventArgs e) => _vm.PlayPause();

    private void OnSettings(object? sender, RoutedEventArgs e) => new SettingsWindow().ShowDialog(this);

    // Repaint the wallpaper gradient + accent glow for the current theme.
    private void ApplyWallpaper()
    {
        var (top, mid, bot, glow) = AppTheme.Wallpaper();
        Root.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.53, 0.85, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(mid, 0.45), new GradientStop(bot, 1) },
        };
        Background = new SolidColorBrush(top);
        Glow.Fill = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(glow, 0), new GradientStop(Color.FromArgb(0, glow.R, glow.G, glow.B), 1) },
        };
    }

    private void OnArtTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackRow r) _vm.PlayRow(r);
        e.Handled = true;
    }
}

