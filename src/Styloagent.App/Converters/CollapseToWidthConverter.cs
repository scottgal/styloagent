using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Styloagent.App.Converters;

/// <summary>
/// Converts a "collapsed" boolean to a <see cref="GridLength"/>: <c>0</c> when collapsed, otherwise
/// the expanded width passed as the converter parameter (e.g. <c>230</c>). Used to collapse the
/// roster / side panels (and their splitters) to give the centre terminals full width.
/// </summary>
public sealed class CollapseToWidthConverter : IValueConverter
{
    public static readonly CollapseToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var collapsed = value is true;
        if (collapsed) return new GridLength(0);

        var expanded = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) => w,
            _ => 0,
        };
        return new GridLength(expanded);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(CollapseToWidthConverter)} does not support ConvertBack.");
}
