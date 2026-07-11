using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using SkiaSharp;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Renders a real markdown doc through MarkdownDocumentView -> LucidMarkdownView. In its own
/// "Avalonia-Markdown" collection (runs last) because LiveMarkdown rendering leaves the shared
/// headless dispatcher in a state that wedges a later test in the main collection.
/// </summary>
[Collection("Avalonia-Markdown")]
public class MarkdownDocumentRenderTests : IDisposable
{
    private readonly HeadlessAvaloniaFixture _fx;
    private readonly string _repoRoot;

    public MarkdownDocumentRenderTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _repoRoot = Path.Combine(Path.GetTempPath(), "mdrender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        File.WriteAllText(Path.Combine(_repoRoot, "readme.md"), "# Readme\n\nRepo document.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    // Asserts the text is actually visible: dark glyph pixels on the light "paper" background.
    [Fact]
    public Task MarkdownDocumentView_renders_visible_markdown_text()
    {
        return _fx.DispatchAsync(async () =>
        {
            const string path = "/tmp/styloagent-markdowndoc.png";
            if (File.Exists(path)) File.Delete(path);

            var mdPath = Path.Combine(_repoRoot, "readme.md");
            var docVm = new MarkdownDocumentViewModel("readme.md", mdPath);
            var view = new MarkdownDocumentView { DataContext = docVm };
            var window = new Window { Width = 640, Height = 480, Content = view };
            window.Show();

            int TextEls() => window.GetVisualDescendants().OfType<TextBlock>().Count();
            for (int i = 0; i < 40 && TextEls() < 1; i++)
            {
                await HeadlessRender.SettleAsync(window);
                await Task.Delay(25);
            }

            Assert.True(TextEls() >= 1, "LucidMarkdownView should render markdown into text blocks");

            await ScreenshotCapture.CaptureControlAsync(window, view, path);
            window.Close();

            using var bmp = SKBitmap.Decode(path);
            Assert.NotNull(bmp);
            int darkText = 0, lightBg = 0;
            for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red < 90 && p.Green < 90 && p.Blue < 90) darkText++;       // glyphs
                if (p.Red > 230 && p.Green > 230 && p.Blue > 230) lightBg++;     // paper
            }
            Assert.True(lightBg > 1000, $"expected a light 'paper' background, found {lightBg} light pixels");
            Assert.True(darkText > 50, $"expected dark markdown glyph pixels on the paper, found {darkText}");
        });
    }
}
