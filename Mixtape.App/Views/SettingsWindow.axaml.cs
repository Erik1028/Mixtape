using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Mixtape.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        BuildAccents();
        BuildVariants();
        BuildLanguages();
    }

    private void BuildLanguages()
    {
        string cur = iPodCommander.Loc.Lang;
        foreach (var (code, native) in iPodCommander.Loc.Languages)
        {
            var btn = new Button { Content = native, Tag = code, FontWeight = code == cur ? FontWeight.Bold : FontWeight.Normal };
            btn.Click += (_, _) =>
            {
                AppConfig.SaveLanguage(code);
                foreach (var b in LanguagePanel.Children.OfType<Button>())
                    b.FontWeight = (string?)b.Tag == code ? FontWeight.Bold : FontWeight.Normal;
                RestartNote.IsVisible = code != iPodCommander.Loc.Lang;   // only prompt when it actually differs from the running language
            };
            LanguagePanel.Children.Add(btn);
        }
    }

    private void BuildAccents()
    {
        foreach (var (name, hex) in AppTheme.Accents)
        {
            var sw = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.Parse(hex)),
                Margin = new Thickness(0, 0, 9, 9),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(name == AppTheme.CurrentAccent ? 2.5 : 0),
                Tag = name,
            };
            sw.PointerPressed += (_, _) =>
            {
                AppTheme.Apply(name, AppTheme.CurrentVariant);
                AppConfig.Save(name, AppTheme.CurrentVariant);
                RefreshAccents();
            };
            AccentPanel.Children.Add(sw);
        }
    }

    private void BuildVariants()
    {
        foreach (var v in AppTheme.Variants)
        {
            var btn = new Button { Content = v, Tag = v };
            btn.Click += (_, _) =>
            {
                AppTheme.Apply(AppTheme.CurrentAccent, v);
                AppConfig.Save(AppTheme.CurrentAccent, v);
                RefreshVariants();
            };
            VariantPanel.Children.Add(btn);
        }
        RefreshVariants();
    }

    private void RefreshAccents()
    {
        foreach (var b in AccentPanel.Children.OfType<Border>())
            b.BorderThickness = new Thickness((string?)b.Tag == AppTheme.CurrentAccent ? 2.5 : 0);
    }

    private void RefreshVariants()
    {
        foreach (var b in VariantPanel.Children.OfType<Button>())
            b.FontWeight = (string?)b.Tag == AppTheme.CurrentVariant ? FontWeight.Bold : FontWeight.Normal;
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
