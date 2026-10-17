using Mostlylucid.ImageSharp.Svg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Styloagent.App.Tests;

public class SvgDependencyTests
{
    [Fact]
    public void Patched_renderer_preserves_transforms_clips_and_png_export()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="60" height="60">
              <defs><clipPath id="clip"><rect width="20" height="20"/></clipPath></defs>
              <rect width="60" height="60" fill="white"/>
              <g transform="translate(10 10)" clip-path="url(#clip)">
                <rect width="40" height="40" fill="red"/>
              </g>
              <rect x="40" y="40" width="10" height="10" fill="blue"/>
            </svg>
            """;
        var result = SvgImage.LoadAsPng(svg);
        using var image = Image.Load<Rgba32>(result.Bytes);
        Assert.Equal(60, image.Width);
        Assert.Equal(new Rgba32(255, 0, 0), image[15, 15]);
        Assert.Equal(new Rgba32(255, 255, 255), image[35, 15]);
        Assert.Equal(new Rgba32(0, 0, 255), image[45, 45]);
    }

    [Fact]
    public void Patched_renderer_preserves_gradients_and_font_outlines()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="60">
              <defs><linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs>
              <rect width="120" height="60" fill="white"/>
              <rect width="120" height="20" fill="url(#g)"/>
              <text x="60" y="50" text-anchor="middle" font-size="22" fill="black">Test</text>
            </svg>
            """;
        using var image = Image.Load<Rgba32>(SvgImage.LoadAsPng(svg).Bytes);
        Assert.True(image[5, 10].R > image[5, 10].B);
        Assert.True(image[115, 10].B > image[115, 10].R);
        var dark = 0;
        for (var y = 22; y < 55; y++)
            for (var x = 20; x < 100; x++)
                if (image[x, y].R < 80 && image[x, y].G < 80 && image[x, y].B < 80) dark++;
        Assert.True(dark > 40, "Font outlines should render visible text after the dependency migration.");
    }
}
