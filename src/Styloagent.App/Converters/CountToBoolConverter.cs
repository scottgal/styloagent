using System.Globalization;
using Avalonia.Data.Converters;

namespace Styloagent.App.Converters;

/// <summary>
/// Converts an integer count to a boolean: returns <c>true</c> when the value is an int greater
/// than zero, <c>false</c> otherwise (including null or non-int values).
/// </summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(CountToBoolConverter)} does not support ConvertBack.");
}
