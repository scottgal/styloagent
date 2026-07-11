using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Styloagent.Core.Git;

namespace Styloagent.App.Converters;

/// <summary>
/// Maps a <see cref="DiffLineKind"/> to a muted background <see cref="SolidColorBrush"/> suitable
/// for colouring diff-view rows.  Added → dark green, Deleted → dark red, Header → dark indigo,
/// Context → transparent.
/// </summary>
public sealed class DiffLineKindBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Added   = new(Color.Parse("#1E3A24"));
    private static readonly SolidColorBrush Deleted = new(Color.Parse("#3A1E1E"));
    private static readonly SolidColorBrush Header  = new(Color.Parse("#2A2A3A"));
    private static readonly SolidColorBrush Context = new(Colors.Transparent);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DiffLineKind kind
            ? kind switch
            {
                DiffLineKind.Added   => Added,
                DiffLineKind.Deleted => Deleted,
                DiffLineKind.Header  => Header,
                _                   => Context,
            }
            : Context;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(DiffLineKindBrushConverter)} does not support ConvertBack.");
}
