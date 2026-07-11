using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Issues;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class IssuesViewTests : IDisposable
{
    private readonly HeadlessAvaloniaFixture _fx;
    private readonly string _issuesDir;

    public IssuesViewTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _issuesDir = Path.Combine(Path.GetTempPath(), "issuesview-" + Guid.NewGuid().ToString("N"));
        IssueStore.Write(_issuesDir, "foss-", "npm build fails on main", "Since the deps bump the build is red.", "high",
            new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.Zero));
        IssueStore.Write(_issuesDir, "docs-", "README missing a quickstart", "New users have no entry point.", "low",
            new DateTimeOffset(2026, 7, 11, 9, 5, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(_issuesDir))
            Directory.Delete(_issuesDir, recursive: true);
    }

    [Fact]
    public Task IssuesView_renders_the_filed_issues()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new IssuesViewModel(_issuesDir);
            var view = new IssuesView { DataContext = vm };
            var window = new Window { Width = 320, Height = 600, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            Assert.Contains(texts, s => s.Contains("npm build fails on main"));
            Assert.Contains(texts, s => s.Contains("README missing a quickstart"));
            Assert.Contains(texts, s => s.Contains("foss-"));
            Assert.Equal(2, vm.OpenCount);

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-issues.png");
            window.Close();
        });
    }
}
