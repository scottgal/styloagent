using System.Globalization;
using Styloagent.App.Converters;

namespace Styloagent.App.Tests;

public class CountToBoolConverterTests
{
    private static object Convert(object? value) =>
        CountToBoolConverter.Instance.Convert(value, typeof(bool), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Zero_returns_false()
        => Assert.Equal(false, Convert(0));

    [Fact]
    public void Positive_count_returns_true()
        => Assert.Equal(true, Convert(3));

    [Fact]
    public void Null_returns_false()
        => Assert.Equal(false, Convert(null));

    [Fact]
    public void ConvertBack_throws_NotSupportedException()
        => Assert.Throws<NotSupportedException>(() =>
            CountToBoolConverter.Instance.ConvertBack(true, typeof(int), null, CultureInfo.InvariantCulture));
}
