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
            // The document surface is themed: under the Dark theme (the app + harness default) it's a
            // dark panel (#0D0D1A) with light text (#E0E0FF) — consistent with the cockpit, not a
            // blinding-white paper. Assert that: a dark background dominates and light glyphs sit on it.
            int lightText = 0, darkBg = 0;
            for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red > 170 && p.Green > 170 && p.Blue > 190) lightText++;   // light glyphs
                if (p.Red < 45 && p.Green < 45 && p.Blue < 65) darkBg++;         // dark panel surface
            }
            Assert.True(darkBg > 1000, $"expected a dark themed document surface, found {darkBg} dark pixels");
            Assert.True(lightText > 50, $"expected light markdown glyph pixels on the dark surface, found {lightText}");
        });
    }
}
