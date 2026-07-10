using System.Globalization;
using Avalonia.Data.Converters;

namespace Styloagent.App.Converters;

/// <summary>Indents a roster row by its depth (12 px per level).</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public static readonly DepthToMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Avalonia.Thickness(value is int d ? d * 12 : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(DepthToMarginConverter)} does not support ConvertBack.");
}
