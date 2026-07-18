using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class AgentPaneChromeViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentPaneChromeViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NullLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NullWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static AgentPaneViewModel MakePane()
    {
        var entry = new AgentManifestEntry("foss-", "/repo", "/repo/wt", "", "", "", AgentTransport.Local);
        var session = new AgentSession(entry, new NullLauncher(), new NullWatcher());
        return new AgentPaneViewModel(session, entry, "foss", "#E57373");
    }

    // Operator fix: the redundant pane-chrome header is GUTTED — the agent name, ⋯ actions menu, zoom and
    // theme moved ONTO the dock tab (see DockTabAgentMenuTests). This asserts the chrome now renders nothing
    // and collapses to zero height, so the terminal reclaims the row (session- deletes the host separately).
    [Fact]
    public Task Chrome_is_gutted_and_collapses_to_zero_height()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            pane.ZoomLevel = 1.5;

            var view = new AgentPaneChromeView { DataContext = pane };
            var host = new StackPanel { Children = { view } };
            var window = new Window { Width = 480, Height = 160, Content = host };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            // Nothing from the old header renders here anymore — it all lives on the dock tab now.
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "⋯");
            Assert.Empty(window.GetVisualDescendants().OfType<Slider>());
            Assert.Empty(window.GetVisualDescendants().OfType<ComboBox>());
            Assert.False(view.IsVisible);
            Assert.Equal(0, view.Bounds.Height);   // collapses → the terminal reclaims the space

            window.Close();
        });
    }

    // The terminal-zoom relay survives the move: session-'s AgentPaneView still binds the hosted
    // TerminalControl.ZoomLevel two-way to VM.ZoomLevel, so the tab's zoom slider zooms the terminal and
    // Ctrl+MouseWheel (which drives TerminalControl.ZoomLevel) moves the value back. (The slider itself
    // now lives on the tab — DockTabAgentMenuTests — not in this pane header.)
    [Fact]
    public Task Zoom_relay_is_two_way_between_the_vm_and_the_terminal()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            var view = new AgentPaneView { DataContext = pane };
            var window = new Window { Width = 640, Height = 400, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var terminal = window.GetVisualDescendants().OfType<Styloagent.Terminal.TerminalControl>().Single();
            Assert.Equal(1.0, terminal.ZoomLevel);   // seam binding initialises the terminal from the VM (1.0)

            pane.ZoomLevel = 2.0;                     // tab-slider path: VM → TerminalControl
            await HeadlessRender.SettleAsync(window);
            Assert.Equal(2.0, terminal.ZoomLevel);

            terminal.ZoomLevel = 1.4;                 // Ctrl+wheel path: TerminalControl → VM (→ tab slider)
            await HeadlessRender.SettleAsync(window);
            Assert.Equal(1.4, pane.ZoomLevel);

            window.Close();
        });
    }
}
