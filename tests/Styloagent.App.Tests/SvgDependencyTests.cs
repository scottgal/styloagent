using Mostlylucid.LucidView.Markdown.Services;
using SkiaSharp;

namespace Styloagent.App.Tests;

public class SvgDependencyTests
{
    [Fact]
    public void Skia_renderer_preserves_transforms_clips_and_png_export()
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
        var result = SvgRasterizer.LoadAsPng(svg);
        using var image = SKBitmap.Decode(result.Bytes);
        Assert.Equal(60, image.Width);
        Assert.Equal(new SKColor(255, 0, 0), image.GetPixel(15, 15));
        Assert.Equal(new SKColor(255, 255, 255), image.GetPixel(35, 15));
        Assert.Equal(new SKColor(0, 0, 255), image.GetPixel(45, 45));
    }

    [Fact]
    public void Skia_renderer_preserves_gradients_and_font_outlines()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="60">
              <defs><linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs>
              <rect width="120" height="60" fill="white"/>
              <rect width="120" height="20" fill="url(#g)"/>
              <text x="60" y="50" text-anchor="middle" font-size="22" fill="black">Test</text>
            </svg>
            """;
        using var image = SKBitmap.Decode(SvgRasterizer.LoadAsPng(svg).Bytes);
        Assert.True(image.GetPixel(5, 10).Red > image.GetPixel(5, 10).Blue);
        Assert.True(image.GetPixel(115, 10).Blue > image.GetPixel(115, 10).Red);
        var dark = 0;
        for (var y = 22; y < 55; y++)
            for (var x = 20; x < 100; x++)
                if (image.GetPixel(x, y).Red < 80 && image.GetPixel(x, y).Green < 80 && image.GetPixel(x, y).Blue < 80) dark++;
        Assert.True(dark > 40, "Font outlines should render visible text after the dependency migration.");
    }
    [Fact]
    public void Cached_local_svg_retains_natural_size_and_renders_at_double_resolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"styloagent-svg-{Guid.NewGuid():N}.svg");
        string? cached = null;
        try
        {
            File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" width="30" height="20"><rect width="30" height="20" fill="red"/></svg>""");
            using var cache = new ImageCacheService();
            cached = cache.CacheLocalSvg(path);
            Assert.NotNull(cached);
            using var image = SKBitmap.Decode(cached);
            Assert.Equal(60, image.Width);
            Assert.Equal(40, image.Height);
            Assert.Equal(SKColors.Red, image.GetPixel(20, 20));
        }
        finally
        {
            File.Delete(path);
            if (cached != null)
            {
                File.Delete(cached);
                File.Delete(cached + ".json");
            }
        }
    }

    [Fact]
    public void Mermaid_diagram_exports_to_png_without_imagesharp()
    {
        var markdown = new MarkdownService();
        using var image = SKBitmap.Decode(markdown.ExportMermaidToPngBytes("graph TD; A[Start]-->B[End]"));
        Assert.NotNull(image);
        Assert.True(image.Width > 20);
        Assert.True(image.Height > 20);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Invalid_scale_is_rejected(float scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SvgRasterizer.LoadAsPng(
            """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>""", scale));
    }
}
