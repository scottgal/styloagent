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
    // and the theme picker all materialize. Compiled bindings (x:DataType) already validate the paths at
    // build; this proves the control renders end-to-end headless.
    [Fact]
    public Task Chrome_renders_identity_actions_menu_and_zoom_readout()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            pane.ZoomLevel = 1.5;   // exercises the two-way zoom relay + readout

            var view = new AgentPaneChromeView { DataContext = pane };
            var window = new Window { Width = 480, Height = 60, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();
            Assert.Contains(texts, s => s.Contains("foss"));       // agent identity
            Assert.Contains(texts, s => s.Contains("150"));         // zoom readout (1.5 → 150%)

            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => (b.Content as string)?.Contains("Actions") == true);

            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), _ => true);   // theme picker
            Assert.Contains(window.GetVisualDescendants().OfType<Slider>(), _ => true);      // zoom slider

            window.Close();
        });
    }
}
