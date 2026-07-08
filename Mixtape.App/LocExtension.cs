using System;
using Avalonia.Markup.Xaml;

namespace Mixtape.App;

/// <summary>XAML markup extension for the shared English-key localization: <c>{l:Loc 'All songs'}</c>.
/// Language is fixed at startup (restart-to-apply), so resolving once at load time is correct.</summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => iPodCommander.Loc.T(Key);
}
