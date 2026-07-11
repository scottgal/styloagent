using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Styloagent.Core.Git;

namespace Styloagent.App.Converters;

/// <summary>
/// Maps a <see cref="DiffLineKind"/> to a row background <see cref="SolidColorBrush"/> that reads
/// correctly in both the Dark and Light application themes.
///
/// Because a value converter is theme-blind (it is not a control and does not participate in
/// resource lookups), the current theme variant is sampled at convert time via
/// <see cref="IsLightTheme"/>.  A theme toggle is therefore reflected on the next converter
/// invocation — e.g., on the next panel refresh or list-box re-selection.  This is the pragmatic
/// approach: it avoids the complexity of a theme-aware trigger while still guaranteeing correct
/// colours after the first refresh.
///
/// Dark:  Added → #1E3A24, Deleted → #3A1E1E, Header → #2A2A3A (matches ThemeTokens Dark entries).
/// Light: Added → #D6F0DD, Deleted → #F5D6D6, Header → #E6E6EE (matches ThemeTokens Light entries).
/// </summary>
public sealed class DiffLineKindBrushConverter : IValueConverter
{
    // Dark-theme backgrounds (deep, muted — light PrimaryTextBrush #E0E0FF reads on them).
    private static readonly SolidColorBrush DarkAdded   = new(Color.Parse("#1E3A24"));
    private static readonly SolidColorBrush DarkDeleted = new(Color.Parse("#3A1E1E"));
    private static readonly SolidColorBrush DarkHeader  = new(Color.Parse("#2A2A3A"));

    // Light-theme backgrounds (pale — dark PrimaryTextBrush #1A1A2E reads on them).
    private static readonly SolidColorBrush LightAdded   = new(Color.Parse("#D6F0DD"));
    private static readonly SolidColorBrush LightDeleted = new(Color.Parse("#F5D6D6"));
    private static readonly SolidColorBrush LightHeader  = new(Color.Parse("#E6E6EE"));

    /// <summary>
    /// Resolves whether the current theme is Light.  Override in tests to control the variant
    /// without requiring <see cref="Avalonia.Application.Current"/> to be non-null.
    /// Defaults to reading <see cref="Avalonia.Application.Current"/>'s actual theme variant.
    /// </summary>
    internal Func<bool> IsLightTheme { get; init; } =
        () => Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as DiffLineKind? ?? DiffLineKind.Context;
        if (kind == DiffLineKind.Context) return Brushes.Transparent;

        bool light = IsLightTheme();
        return kind switch
        {
            DiffLineKind.Added   => light ? LightAdded   : DarkAdded,
            DiffLineKind.Deleted => light ? LightDeleted : DarkDeleted,
            DiffLineKind.Header  => light ? LightHeader  : DarkHeader,
            _                   => Brushes.Transparent,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(DiffLineKindBrushConverter)} does not support ConvertBack.");
}
