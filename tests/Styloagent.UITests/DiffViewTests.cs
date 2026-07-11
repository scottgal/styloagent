using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Git;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class DiffViewTests(HeadlessAvaloniaFixture fx) : IDisposable
{
    public void Dispose() { }

    [Fact]
    public Task DiffView_renders_coloured_diff_lines()
    {
        return fx.DispatchAsync(async () =>
        {
            var vm = new DiffViewModel
            {
                File = new FileDiff("Foo.cs", 1, 1, false, new[]
                {
                    new DiffLine(DiffLineKind.Header,  "@@ -1,2 +1,2 @@", 0, 0),
                    new DiffLine(DiffLineKind.Context, "unchanged",        1, 1),
                    new DiffLine(DiffLineKind.Deleted, "old",              2, 0),
                    new DiffLine(DiffLineKind.Added,   "new",              0, 2),
                }),
            };

            var view   = new DiffView { DataContext = vm };
            var window = new Window { Width = 640, Height = 400, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            Assert.Contains(texts, s => s.Contains("new"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-diff.png");
            window.Close();
        });
    }
}
