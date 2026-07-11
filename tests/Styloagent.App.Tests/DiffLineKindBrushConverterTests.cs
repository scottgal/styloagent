using System.Globalization;
using Avalonia.Media;
using Styloagent.App.Converters;
using Styloagent.Core.Git;

namespace Styloagent.App.Tests;

public class DiffLineKindBrushConverterTests
{
    private static readonly DiffLineKindBrushConverter Converter = new();

    private static SolidColorBrush Convert(DiffLineKind kind) =>
        (SolidColorBrush)Converter.Convert(kind, typeof(SolidColorBrush), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Added_returns_green_brush()
    {
        var brush = Convert(DiffLineKind.Added);
        Assert.Equal(Color.Parse("#1E3A24"), brush.Color);
    }

    [Fact]
    public void Deleted_returns_red_brush()
    {
        var brush = Convert(DiffLineKind.Deleted);
        Assert.Equal(Color.Parse("#3A1E1E"), brush.Color);
    }

    [Fact]
    public void Header_returns_indigo_brush()
    {
        var brush = Convert(DiffLineKind.Header);
        Assert.Equal(Color.Parse("#2A2A3A"), brush.Color);
    }

    [Fact]
    public void Context_returns_transparent_brush()
    {
        var brush = Convert(DiffLineKind.Context);
        Assert.Equal(Colors.Transparent, brush.Color);
    }

    [Fact]
    public void Null_value_returns_transparent_brush()
    {
        var brush = (SolidColorBrush)Converter.Convert(null, typeof(SolidColorBrush), null, CultureInfo.InvariantCulture);
        Assert.Equal(Colors.Transparent, brush.Color);
    }

    [Fact]
    public void ConvertBack_throws_NotSupportedException()
        => Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack(null, typeof(DiffLineKind), null, CultureInfo.InvariantCulture));
}
