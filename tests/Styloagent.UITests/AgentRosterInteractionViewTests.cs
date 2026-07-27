using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public sealed class AgentRosterInteractionViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentRosterInteractionViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public Task Roster_has_a_distinct_expander_and_selecting_a_tab_does_not_expand_it()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sty-roster-interaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "saved-context"));
            await File.WriteAllTextAsync(Path.Combine(root, "saved-context", "foss-context.md"), "# Saved context");
            MainWindowViewModel? vm = null;
            Window? window = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new FakePtyLauncher(), new NoWatcher());
                vm.Pane!.ContextFraction = .72;
                vm.Pane.UsageText = "28k left · 72% used";
                var view = new AgentsView { DataContext = vm };
                window = new Window { Width = 360, Height = 260, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "RosterExpander");
                Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "AgentTabSelector");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Context");
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/styloagent-roster-expander.png", settle: true);

                await using var session = await UITestSession.AttachAsync(window);
                await session.ClickAsync("name=AgentTabSelector");
                Assert.False(vm.Pane.IsRosterExpanded);

                await session.ClickAsync("name=RosterExpander");
                await HeadlessRender.SettleAsync(window);
                Assert.True(vm.Pane.IsRosterExpanded);
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/styloagent-roster-expanded.png", settle: true);
            }
            finally
            {
                window?.Close();
                vm?.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
