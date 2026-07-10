using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using SkiaSharp;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class DocLibraryViewTests : IDisposable
{
    private readonly HeadlessAvaloniaFixture _fx;
    private readonly string _repoRoot;
    private readonly string _channelRoot;

    public DocLibraryViewTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _repoRoot = Path.Combine(Path.GetTempPath(), "doclibview-repo-" + Guid.NewGuid().ToString("N"));
        _channelRoot = Path.Combine(Path.GetTempPath(), "doclibview-channel-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_channelRoot);

        File.WriteAllText(Path.Combine(_repoRoot, "readme.md"), "# Readme\n\nRepo document.");
        File.WriteAllText(Path.Combine(_channelRoot, "notes.md"), "# Notes\n\nChannel document.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public Task DocLibraryView_renders_group_headers_and_doc_buttons()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
            var view = new DocLibraryView { DataContext = vm };
            var window = new Window { Width = 320, Height = 600, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            // Group header text should be visible
            Assert.Contains(texts, s => s.Contains("repo"));
            Assert.Contains(texts, s => s.Contains("channel"));

            // At least one doc entry button should have materialized
            Assert.Contains(texts, s => s.Contains("readme.md") || s.Contains("notes.md"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-doclibrary.png");
            window.Close();
        });
    }

    // Renders a real markdown doc through MarkdownDocumentView -> LucidMarkdownView and asserts the
    // text is actually visible: dark glyph pixels on the light "paper" background. Guards both that
    // the render realizes headless AND that the doc stays readable (not white-on-white / dark-on-dark).
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

            // LiveMarkdown renders asynchronously (fire-and-forget render + streaming builder), so
            // wait on the condition rather than a single settle — poll until the text blocks
            // materialize (deterministic; avoids the flakiness of a fixed settle under suite load).
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
