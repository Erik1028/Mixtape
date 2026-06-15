using Avalonia.Controls;
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
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
}
