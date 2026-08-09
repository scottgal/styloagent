using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Model;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class WelcomeViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public WelcomeViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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
            var recentScroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(s => s.GetVisualDescendants().OfType<ItemsControl>().Any());
            Assert.NotNull(recentScroller);
            Assert.NotNull(window.GetVisualDescendants().OfType<Image>().FirstOrDefault(i => i.Source is not null));
            Assert.NotNull(Toggle(window, "ClaudeFirstToggle"));
            Assert.NotNull(Toggle(window, "CodexFirstToggle"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-welcome.png");
            window.Close();
        });
    }

    [Fact]
    public Task WelcomeView_runtime_toggle_sets_codex_first()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml", new FakePicker(), _ => { });
            var view = new WelcomeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 520, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            Toggle(window, "CodexFirstToggle")!.Command!.Execute("Codex");

            Assert.Equal(AgentRuntimeKind.Codex, vm.SelectedRuntime);
            Assert.True(vm.IsCodexFirst);
            Assert.False(vm.IsClaudeFirst);

            window.Close();
        });
    }

    /// <summary>
    /// The reported regression: starting with Claude selected, clicking "Claude + DS" must switch the
    /// selection — Claude must UNselect, Claude+DS must select, and the persisted default must follow.
    /// A real ToggleButton click both toggles IsChecked AND fires the command, so the test mimics both.
    /// </summary>
    [Fact]
    public Task Clicking_a_new_runtime_card_switches_selection_and_visual()
    {
        return _fx.DispatchAsync(async () =>
        {
            AgentRuntimeKind? saved = null;
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml", new FakePicker(), _ => { },
                initialRuntime: AgentRuntimeKind.Claude, onRuntimeChanged: kind => saved = kind);
            var view = new WelcomeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 520, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var claude = Toggle(window, "ClaudeFirstToggle")!;
            var cds = Toggle(window, "ClaudeDeepSeekFirstToggle")!;
            Assert.True(claude.IsChecked, "claude should be selected initially");

            // Real-click semantics on the Claude+DS card: toggle IsChecked (TwoWay writes SelectedRuntime)
            // then fire the command (re-push + persist).
            cds.IsChecked = true;
            cds.Command!.Execute("ClaudeDeepSeek");

            Assert.Equal(AgentRuntimeKind.ClaudeDeepSeek, vm.SelectedRuntime);
            Assert.Equal(AgentRuntimeKind.ClaudeDeepSeek, saved);
            Assert.True(vm.IsClaudeDeepSeekFirst);
            Assert.True(cds.IsChecked, "Claude+DS card must end checked");
            Assert.False(vm.IsClaudeFirst);
            Assert.False(claude.IsChecked, "Claude card must unselect when a new runtime is picked");

            window.Close();
        });
    }

    private static ToggleButton? Toggle(Window window, string name)
        => window.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == name);
}
