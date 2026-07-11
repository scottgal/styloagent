using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
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
}
