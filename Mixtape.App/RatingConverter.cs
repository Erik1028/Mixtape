using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Mixtape.App;

/// <summary>Picks the glyph for one star slot in the RATING column: a FILLED star (★) when its slot index
/// (ConverterParameter "1".."5") is within the row's <c>RatingValue</c>, else a hollow one (☆). The star's
/// colour comes from the themed AccentBrush in XAML, so this only decides filled-vs-hollow — no colour here,
/// which keeps the stars following the accent when the theme changes.</summary>
public sealed class StarGlyphConverter : IValueConverter
{
    public static readonly StarGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value is int r ? r : 0;
        int slot = parameter is string s && int.TryParse(s, out var p) ? p : 0;
        return slot >= 1 && slot <= rating ? "★" : "☆";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>Up-Next row background: the now-playing row gets the accent whisper tint (shared SelRowBrush),
/// every other row is transparent.</summary>
public sealed class NowPlayingBgConverter : IValueConverter
{
    public static readonly NowPlayingBgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool now = value is bool b && b;
        if (!now) return Avalonia.Media.Brushes.Transparent;
        return Avalonia.Application.Current?.Resources["SelRowBrush"] as Avalonia.Media.IBrush
               ?? Avalonia.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>Colour companion of <see cref="StarGlyphConverter"/>: filled slots get the themed accent,
/// hollow slots the faint grey (the Windows list draws empty stars in Theme.Faint, not accent). Returns
/// the SHARED app brushes, so the stars keep following a live accent change.</summary>
public sealed class StarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value is int r ? r : 0;
        int slot = parameter is string s && int.TryParse(s, out var p) ? p : 0;
        string key = slot >= 1 && slot <= rating ? "AccentBrush" : "FaintBrush";
        return Avalonia.Application.Current?.Resources[key] as Avalonia.Media.IBrush
               ?? Avalonia.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
