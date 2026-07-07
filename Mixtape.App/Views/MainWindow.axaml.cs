using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        // The 60° gradient's endpoint depends on the window's aspect ratio (GDI+ angle-mode brushes
        // normalise the stops over the rect's projection onto the axis) — recompute on resize.
        SizeChanged += (_, _) => ApplyWallpaper();

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
        // Test aid: `--showflyout upnext|eq` queues a couple tracks (for upnext) then opens the flyout.
        var foArgs = Environment.GetCommandLineArgs();
        int foi = System.Array.IndexOf(foArgs, "--showflyout");
        if (foi >= 0 && foi + 1 < foArgs.Length)
        {
            string which = foArgs[foi + 1];
            var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) =>
            {
                if (!_vm.CoverFlowAvailable) return;   // wait for the scan
                t.Stop();
                Activate();
                if (_vm.Tracks.Count > 0) _vm.PlayRow(_vm.Tracks[0]);
                if (which == "upnext" && _vm.Tracks.Count > 2) _vm.QueueAdd(new[] { _vm.Tracks[1], _vm.Tracks[2] });
                // let the now-playing bar realise before opening its attached flyout
                var t2 = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                t2.Tick += (_, _) => { t2.Stop(); FlyoutBase.ShowAttachedFlyout(which == "upnext" ? QueueBtn : EqBtn); };
                t2.Start();
            };
            t.Start();
        }

        // Test aid: `--coverflow [mode]` auto-opens Cover Flow shortly after launch (for headless screenshotting).
        var cfArgs = Environment.GetCommandLineArgs();
        if (cfArgs.Contains("--coverflow"))
        {
            int mi = System.Array.IndexOf(cfArgs, "--coverflow");
            if (mi >= 0 && mi + 1 < cfArgs.Length && !cfArgs[mi + 1].StartsWith("-")) CoverFlow.SetMode(cfArgs[mi + 1]);
            var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (_, _) => { if (_vm.CoverFlowAvailable) { t.Stop(); OnCoverFlow(this, new RoutedEventArgs()); } };
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

    private void OnClearSearch(object? sender, RoutedEventArgs e) => _vm.SearchText = "";

    // ---- custom title bar (we draw our own caption + window buttons) ----
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && MaxButton is not null)
            MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    // The ▶ in the sidebar header: play the selected (or first) track of the current view.
    private void OnPlayFile(object? sender, RoutedEventArgs e)
    {
        var row = (SongGrid.SelectedItem as TrackRow) ?? _vm.Tracks.FirstOrDefault();
        if (row is not null) _vm.PlayRow(row);
    }

    // The sidebar "Open folder" button: reveal the iPod / Local Music folder in the OS file manager.
    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var path = _vm.OpenableFolder;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { _vm.Status = "No folder to open."; return; }
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", path);
            else System.Diagnostics.Process.Start("xdg-open", path);
        }
        catch (Exception ex) { _vm.Status = "Couldn't open folder: " + ex.Message; }
    }

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
    private void OnPrev(object? sender, RoutedEventArgs e) => _vm.Previous();
    private void OnNext(object? sender, RoutedEventArgs e) => _vm.Next();
    private void OnShuffle(object? sender, RoutedEventArgs e) => _vm.ToggleShuffle();
    private void OnRepeat(object? sender, RoutedEventArgs e) => _vm.CycleRepeat();
    private void OnMute(object? sender, RoutedEventArgs e) => _vm.ToggleMute();
    private void OnEq(object? sender, RoutedEventArgs e) { if (sender is Control c) FlyoutBase.ShowAttachedFlyout(c); }
    private void OnEqFlat(object? sender, RoutedEventArgs e) => _vm.EqFlat();
    private void OnQueue(object? sender, RoutedEventArgs e) { if (sender is Control c) FlyoutBase.ShowAttachedFlyout(c); }
    private void OnQueueClear(object? sender, RoutedEventArgs e) => _vm.QueueClear();
    private void OnQueueRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MainViewModel.QueueRow q }) _vm.QueueRemove(q.Row);
    }
    private void OnQueueRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MainViewModel.QueueRow q } && !q.IsNowPlaying) _vm.JumpTo(q.Row);
    }

    private System.Collections.Generic.List<TrackRow> SelectedRows()
        => SongGrid.SelectedItems.OfType<TrackRow>().ToList();
    private void OnCtxPlay(object? sender, RoutedEventArgs e)
    {
        var r = SongGrid.SelectedItem as TrackRow ?? SelectedRows().FirstOrDefault();
        if (r is not null) _vm.PlayRow(r);
    }
    private void OnCtxPlayNext(object? sender, RoutedEventArgs e) => _vm.QueuePlayNext(SelectedRows());
    private void OnCtxAddQueue(object? sender, RoutedEventArgs e) => _vm.QueueAdd(SelectedRows());

    // ---- Cover Flow ----
    private bool _coverFlowWired;
    private void OnCoverFlow(object? sender, RoutedEventArgs e)
    {
        if (!_coverFlowWired)
        {
            CoverFlow.Activated += it => { _vm.CoverFlowActivate(it.Tag); CoverFlow.PlayingTag = it.Tag; };
            CoverFlow.CloseRequested += () => CoverFlow.IsVisible = false;
            CoverFlow.ModeChanged += _ => PopulateCoverFlow();
            _coverFlowWired = true;
        }
        PopulateCoverFlow();
        CoverFlow.IsVisible = true;
        CoverFlow.Focus();
    }
    private void PopulateCoverFlow()
    {
        var (items, start, playing) = _vm.BuildCoverFlow(CoverFlow.Mode);
        CoverFlow.PlayingTag = playing;
        CoverFlow.SetItems(items, start);
    }

    private void OnSettings(object? sender, RoutedEventArgs e) => new SettingsWindow().ShowDialog(this);

    // Repaint the wallpaper (60° 4-stop gradient + both glows) and the theme-baked gradients
    // (scroll-edge fade, idle now-playing tile) for the current theme.
    private void ApplyWallpaper()
    {
        var (a0, a1, mid, a3, glow1, glow2) = AppTheme.Wallpaper();
        // GDI+ angle-mode gradient at 60°: stops 0..1 span the rect's projection onto the 60° axis,
        // so the axis endpoint is the far corner's projection — aspect-ratio dependent.
        double w = Math.Max(1, Root.Bounds.Width > 0 ? Root.Bounds.Width : Width);
        double h = Math.Max(1, Root.Bounds.Height > 0 ? Root.Bounds.Height : Height);
        const double cos60 = 0.5, sin60 = 0.8660254;
        double pmax = w * cos60 + h * sin60;
        Root.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(pmax * cos60 / w, pmax * sin60 / h, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(a0, 0), new GradientStop(a1, 0.30),
                new GradientStop(mid, 0.62), new GradientStop(a3, 1),
            },
        };
        Background = new SolidColorBrush(a0);
        Glow.Fill = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(glow1, 0), new GradientStop(Color.FromArgb(0, glow1.R, glow1.G, glow1.B), 1) },
        };
        Glow2.Fill = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(glow2, 0), new GradientStop(Color.FromArgb(0, glow2.R, glow2.G, glow2.B), 1) },
        };

        // Scroll-edge fade: rows dissolve into the CARD colour, so it must follow the theme's Bg.
        var bg = (Application.Current?.Resources["AppBrush"] as SolidColorBrush)?.Color ?? Color.Parse("#1D1E22");
        ScrollFade.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(bg, 0), new GradientStop(Color.FromArgb(0, bg.R, bg.G, bg.B), 1) },
        };

        // Idle now-playing art placeholder: neutral sidebar-derived tile.
        var (tileTop, tileBot) = AppTheme.IdleTile();
        NowTile.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0.866, RelativeUnit.Relative),
            GradientStops = { new GradientStop(tileTop, 0), new GradientStop(tileBot, 1) },
        };
    }

    private void OnArtTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackRow r) _vm.PlayRow(r);
        e.Handled = true;
    }

    // Click a star in the RATING column → set (or toggle-clear) that track's rating on the iPod.
    // The button's DataContext is its row's TrackRow; its Tag ("1".."5") is the star level.
    private void OnStarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is TrackRow row)
        {
            int star = b.Tag is string s && int.TryParse(s, out var p) ? p : 0;
            _vm.RateTrack(row, star);
        }
    }
}

