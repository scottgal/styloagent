using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Styloagent.App.Converters;

/// <summary>
/// Maps an issue severity string (low | medium | high) to a status colour for the severity dot.
/// Unknown values fall back to the medium colour.
/// </summary>
public sealed class SeverityColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Low = new(Color.Parse("#5AA0E5"));
    private static readonly SolidColorBrush Medium = new(Color.Parse("#E5A05A"));
    private static readonly SolidColorBrush High = new(Color.Parse("#E5615A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.Trim().ToLowerInvariant() switch
        {
            "low" => Low,
            "high" => High,
            _ => Medium,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(SeverityColorConverter)} does not support ConvertBack.");
}
