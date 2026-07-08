using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mixtape.App.Views;

/// <summary>Tiny code-built modal dialogs (text prompt + yes/no confirm), themed with the app brushes.
/// Avalonia has no built-in input dialog, so these back the playlist create/rename/delete flows.</summary>
internal static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string okText, string initial = "")
    {
        var box = new TextBox { Text = initial, Watermark = "", MinWidth = 300, FontSize = 14 };
        var dlg = Shell(owner, title, box, okText, out var ok, out var cancel);
        string? result = null;
        ok.Click += (_, _) => { result = box.Text ?? ""; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = box.Text ?? ""; dlg.Close(); } else if (e.Key == Key.Escape) { result = null; dlg.Close(); } };
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dlg.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? (result is null ? null : result) : result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string okText)
    {
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 13, Foreground = App.Brush("SubtleBrush") };
        var dlg = Shell(owner, title, msg, okText, out var ok, out var cancel);
        bool result = false;
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => { result = false; dlg.Close(); };
        await dlg.ShowDialog(owner);
        return result;
    }

    private static Window Shell(Window owner, string title, Control body, string okText, out Button ok, out Button cancel)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = App.Brush("TextBrush") };
        ok = new Button { Content = okText, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");
        cancel = new Button { Content = "Cancel", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);

        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(buttons);

        var card = new Border { Background = App.Brush("SidebarBrush"), CornerRadius = new CornerRadius(14), Child = stack };

        return new Window
        {
            Title = title,
            Content = card,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = App.Brush("AppBrush"),
            ShowInTaskbar = false,
        };
    }
}
