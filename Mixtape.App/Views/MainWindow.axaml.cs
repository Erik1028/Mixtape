using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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

    // ---- custom window chrome ----
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void OnCaptionDoubleTapped(object? sender, TappedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

