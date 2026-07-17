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

    // Renders the real cockpit-owned pane chrome (0b) over an AgentPaneViewModel and asserts the identity,
    // the consolidated ⋯ actions button, the zoom readout (proving the ZoomLevel relay binding resolves)
    // and the zoom slider all materialize. Compiled bindings (x:DataType) already validate the paths at
    // build; this proves the control renders end-to-end headless.
    //
    // Compaction: the theme picker moved OFF the header into the ⋯ menu (so no ComboBox renders in the
    // header until the flyout is opened) and the header row is short — the operator's "way too tall" fix.
    [Fact]
    public Task Chrome_is_compact_with_identity_actions_menu_and_zoom_readout()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            pane.ZoomLevel = 1.5;   // exercises the two-way zoom relay + readout

            var view = new AgentPaneChromeView { DataContext = pane };
            // StackPanel host gives the chrome its natural (unstretched) height so we can measure it.
            var host = new StackPanel { Children = { view } };
            var window = new Window { Width = 480, Height = 160, Content = host };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();
            Assert.Contains(texts, s => s.Contains("foss"));       // agent identity (also captions the tab)
            Assert.Contains(texts, s => s.Contains("150"));         // zoom readout (1.5 → 150%)

            // The actions menu collapsed to a little ⋯ icon button (was "⋯ Actions") to free space.
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => (b.Content as string) == "⋯");

            Assert.Contains(window.GetVisualDescendants().OfType<Slider>(), _ => true);      // zoom slider
            // The theme picker moved into the ⋯ menu; its ComboBox is in the (unopened) flyout, not the header.
            Assert.Empty(window.GetVisualDescendants().OfType<ComboBox>());

            // Compact: the header row is short (the "way too tall" fix). The old bar was ~40px; assert well under.
            Assert.True(view.Bounds.Height <= 30, $"pane chrome header too tall: {view.Bounds.Height}px");

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-pane-chrome-compact.png");
            window.Close();
        });
    }

    // Integrated proof of session-'s host seam (5271a32): the REAL AgentPaneView hosts the chrome, and the
    // VM ZoomLevel relay is two-way-wired to the hosted TerminalControl.ZoomLevel — so the slider zooms the
    // terminal and Ctrl+MouseWheel (which drives TerminalControl.ZoomLevel) moves the slider back. My other
    // test covers the chrome in isolation; this proves the end-to-end wiring through session-'s AgentPaneView.
    [Fact]
    public Task AgentPaneView_hosts_the_chrome_and_the_zoom_relay_is_two_way_to_the_terminal()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            var view = new AgentPaneView { DataContext = pane };
            var window = new Window { Width = 640, Height = 400, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            // The cockpit chrome is materialized in the real pane header.
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(texts, s => s.Contains("foss"));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
                b => (b.Content as string) == "⋯");
            Assert.Contains(window.GetVisualDescendants().OfType<Slider>(), _ => true);

            var terminal = window.GetVisualDescendants().OfType<Styloagent.Terminal.TerminalControl>().Single();
            Assert.Equal(1.0, terminal.ZoomLevel);   // seam binding initialises the terminal from the VM (1.0)

            pane.ZoomLevel = 2.0;                     // slider path: VM → TerminalControl
            await HeadlessRender.SettleAsync(window);
            Assert.Equal(2.0, terminal.ZoomLevel);

            terminal.ZoomLevel = 1.4;                 // Ctrl+wheel path: TerminalControl → VM (→ slider)
            await HeadlessRender.SettleAsync(window);
            Assert.Equal(1.4, pane.ZoomLevel);

            window.Close();
        });
    }
}
