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
