using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class WelcomeAndProposedTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public WelcomeAndProposedTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakePicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public Task WelcomeView_renders_open_button_and_recents()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml", new FakePicker(), _ => { });
            vm.Recent.Add("/a/recent/project");
            var view = new WelcomeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 380, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, s => s.Contains("Open a project"));
            Assert.Contains(texts, s => s.Contains("/a/recent/project"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-welcome.png");
            window.Close();
        });
    }
}
