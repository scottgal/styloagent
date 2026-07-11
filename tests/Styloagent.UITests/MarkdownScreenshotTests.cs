using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Captures the markdown-doc README screenshot. In the "Avalonia-Markdown" collection (runs last)
/// because LiveMarkdown rendering wedges a later test in the shared headless session.
/// </summary>
[Collection("Avalonia-Markdown")]
public class MarkdownScreenshotTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public MarkdownScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static string Shot(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Styloagent.App")))
            dir = dir.Parent;
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        var shots = Path.Combine(root, "docs", "screenshots");
        Directory.CreateDirectory(shots);
        return Path.Combine(shots, name);
    }

    [Fact]
    public Task Capture_markdown_doc()
    {
        return _fx.DispatchAsync(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "shot-md-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var mdPath = Path.Combine(dir, "PROTOCOL.md");
            try
            {
                File.WriteAllText(mdPath,
                    "# Signal Bus Protocol\n\n" +
                    "Agents coordinate over a **file-drop** channel. Each message is a markdown file.\n\n" +
                    "## Routing\n\n" +
                    "- `inbox/` — messages awaiting a reply\n" +
                    "- `outbox/` — replies\n" +
                    "- `archive/` — resolved threads\n\n" +
                    "A thread is *replied* once an `outbox/<slug>.reply.md` exists.\n");

                var docVm = new MarkdownDocumentViewModel("PROTOCOL.md", mdPath);
                var view = new MarkdownDocumentView { DataContext = docVm };
                var window = new Window { Width = 560, Height = 400, Content = view };
                window.Show();

                int TextEls() => window.GetVisualDescendants().OfType<TextBlock>().Count();
                for (int i = 0; i < 40 && TextEls() < 1; i++)
                {
                    await HeadlessRender.SettleAsync(window);
                    await Task.Delay(25);
                }
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("markdown-doc.png"));
                window.Close();
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        });
    }
}
