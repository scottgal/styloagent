using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Operator: a rate-limited agent must read as throttled, not falsely "working". Renders the real roster row
/// against a throttled pane and asserts the amber ⏳ "throttled" badge materializes.
/// </summary>
[Collection("Avalonia")]
public class ThrottleBadgeViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ThrottleBadgeViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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
        var entry = new AgentManifestEntry("rl-", "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
        return new AgentPaneViewModel(new AgentSession(entry, new NullLauncher(), new NullWatcher()), entry, "rl", "#E5A05A");
    }

    [Fact]
    public Task Throttled_pane_shows_the_throttled_badge_in_the_roster_row()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            pane.IsThrottled = true;
            pane.LastThrottleSignature = "429";

            var template = (IDataTemplate)new AgentsView().Resources["AgentRowTemplate"]!;
            var host = new ContentControl { Width = 260, Height = 56, ContentTemplate = template, Content = pane };
            var window = new Window { Width = 280, Height = 72, Content = host };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, s => s == "throttled");
            Assert.Contains(texts, s => s == "⏳");

            // Not throttled → no badge.
            pane.IsThrottled = false;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            var visibleThrottle = window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => (t.Text ?? "") == "throttled" && t.GetVisualAncestors().OfType<Border>().All(b => b.IsVisible));
            Assert.False(visibleThrottle);

            window.Close();
        });
    }
}
