using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Styloagent.App.Converters;
using Xunit;

namespace Styloagent.App.Tests;

public class CollapseToWidthConverterTests
{
    private static GridLength Convert(object? value, string param) =>
        (GridLength)CollapseToWidthConverter.Instance.Convert(value, typeof(GridLength), param, CultureInfo.InvariantCulture);

    [Fact]
    public void Collapsed_is_zero_width()
    {
        var r = Convert(true, "230");
        Assert.Equal(0, r.Value);
    }

    [Fact]
    public void Expanded_uses_the_parameter_width()
    {
        var r = Convert(false, "230");
        Assert.Equal(230, r.Value);
        Assert.True(r.IsAbsolute);
    }

    [Fact]
    public void Non_true_value_is_treated_as_expanded()
    {
        Assert.Equal(300, Convert(null, "300").Value);
    }
}
