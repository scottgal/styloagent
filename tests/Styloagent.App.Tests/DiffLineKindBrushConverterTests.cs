using System.Globalization;
using Avalonia.Media;
using Styloagent.App.Converters;
using Styloagent.Core.Git;

namespace Styloagent.App.Tests;

public class DiffLineKindBrushConverterTests
{
    // Converters wired to a fixed theme so tests are deterministic without a running
    // Avalonia app (Application.Current is null in pure unit tests).
    private static readonly DiffLineKindBrushConverter DarkConverter  = new() { IsLightTheme = () => false };
    private static readonly DiffLineKindBrushConverter LightConverter = new() { IsLightTheme = () => true  };

    private static ISolidColorBrush Convert(DiffLineKindBrushConverter converter, DiffLineKind kind) =>
        (ISolidColorBrush)converter.Convert(kind, typeof(SolidColorBrush), null, CultureInfo.InvariantCulture);

    // --- Dark theme ---

    [Fact]
    public void Dark_Added_returns_dark_green_brush()
    {
        var brush = Convert(DarkConverter, DiffLineKind.Added);
        Assert.Equal(Color.Parse("#1E3A24"), brush.Color);
    }

    [Fact]
    public void Dark_Deleted_returns_dark_red_brush()
    {
        var brush = Convert(DarkConverter, DiffLineKind.Deleted);
        Assert.Equal(Color.Parse("#3A1E1E"), brush.Color);
    }

    [Fact]
    public void Dark_Header_returns_dark_indigo_brush()
    {
        var brush = Convert(DarkConverter, DiffLineKind.Header);
        Assert.Equal(Color.Parse("#2A2A3A"), brush.Color);
    }

    [Fact]
    public void Dark_Context_returns_transparent_brush()
    {
        var brush = Convert(DarkConverter, DiffLineKind.Context);
        Assert.Equal(Colors.Transparent, brush.Color);
    }

    // --- Light theme ---

    [Fact]
    public void Light_Added_returns_pale_green_brush()
    {
        var brush = Convert(LightConverter, DiffLineKind.Added);
        Assert.Equal(Color.Parse("#D6F0DD"), brush.Color);
    }

    [Fact]
    public void Light_Deleted_returns_pale_red_brush()
    {
        var brush = Convert(LightConverter, DiffLineKind.Deleted);
        Assert.Equal(Color.Parse("#F5D6D6"), brush.Color);
    }

    [Fact]
    public void Light_Header_returns_pale_grey_brush()
    {
        var brush = Convert(LightConverter, DiffLineKind.Header);
        Assert.Equal(Color.Parse("#E6E6EE"), brush.Color);
    }

    [Fact]
    public void Light_Context_returns_transparent_brush()
    {
        var brush = Convert(LightConverter, DiffLineKind.Context);
        Assert.Equal(Colors.Transparent, brush.Color);
    }

    // --- Null / ConvertBack ---

    [Fact]
    public void Null_value_returns_transparent_brush()
    {
        var brush = (ISolidColorBrush)DarkConverter.Convert(null, typeof(SolidColorBrush), null, CultureInfo.InvariantCulture);
        Assert.Equal(Colors.Transparent, brush.Color);
    }

    [Fact]
    public void ConvertBack_throws_NotSupportedException()
        => Assert.Throws<NotSupportedException>(() =>
            DarkConverter.ConvertBack(null, typeof(DiffLineKind), null, CultureInfo.InvariantCulture));
}
