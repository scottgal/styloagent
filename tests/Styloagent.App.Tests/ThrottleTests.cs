using System.Linq;
using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// API-throttle / rate-limit badge (session- detects via ApiThrottleDetector; cockpit- renders): a
/// transient per-agent flag (NOT a hook state) so a rate-limited agent reads as throttled, not "working".
/// </summary>
public class ThrottleTests
{
    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 3000)
    {
        for (int w = 0; w < timeoutMs && !cond(); w += 10) await Task.Delay(10);
    }

    [Fact]
    public void ApplyThrottleEvent_sets_flags_when_throttled_and_clears_since_when_resumed()
    {
        var entry = new AgentManifestEntry("rl-", "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
        var pane = new AgentPaneViewModel(new AgentSession(entry, new FakeLauncher(), new FakeWatcher()),
            entry, "rl", "#888888");
        var at = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        MainWindowViewModel.ApplyThrottleEvent(pane, new ThrottleEvent("rl-", true, "429", at));
        Assert.True(pane.IsThrottled);
        Assert.Equal("429", pane.LastThrottleSignature);
        Assert.Equal(at, pane.ThrottledSince);
        Assert.Contains("429", pane.ThrottleTooltip);

        MainWindowViewModel.ApplyThrottleEvent(pane, new ThrottleEvent("rl-", false, "429", at));
        Assert.False(pane.IsThrottled);
        Assert.Null(pane.ThrottledSince);   // no longer throttled → no "since"
    }

    [Fact]
    public async Task Agent_rate_limit_output_flips_the_pane_to_throttled()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            MainWindowViewModelTests.MakeTwoAgentChannel(), new FakeLauncher(), new FakeWatcher());

        var launcher = new FakeLauncher();
        var entry = new AgentManifestEntry("rl-", "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
        var pane = new AgentPaneViewModel(new AgentSession(entry, launcher, new FakeWatcher()), entry, "rl", "#888888");
        await pane.SpawnAsync();          // FakeLauncher → a FakePty; the session subscribes to its output
        vm.Panes.Add(pane);               // shell wires an ApiThrottleDetector to the pane's output

        Assert.False(pane.IsThrottled);
        // A rate-limit banner in the agent's PTY output → the detector fires → the pane flips to throttled.
        launcher.Spawned[0].FireOutput("⎿  API Error: 429 overloaded_error — retrying");
        await WaitUntil(() => pane.IsThrottled);

        Assert.True(pane.IsThrottled);
        Assert.False(string.IsNullOrEmpty(pane.LastThrottleSignature));
        Assert.NotNull(pane.ThrottledSince);
    }

    // Throttle Part 2: session-'s ThrottleRetryScheduler's postRetry hook. When a throttled agent doesn't
    // self-clear, the scheduler calls PostThrottleRetryAsync — which must post a VISIBLE, escalating retry
    // bus message to that agent (the detected signature in the body), riding the delivery→injector path.
    [Fact]
    public async Task PostThrottleRetryAsync_posts_an_escalating_retry_bus_message_to_the_throttled_agent()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            var vm = await MainWindowViewModel.InitializeAsync(root, launcher, new FakeWatcher());

            var entry = new AgentManifestEntry("rl-", "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
            var pane = new AgentPaneViewModel(new AgentSession(entry, launcher, new FakeWatcher()), entry, "rl", "#888888");
            await pane.SpawnAsync();               // Live → SendBusMessage won't try to rehydrate it
            pane.LastThrottleSignature = "429";    // the scheduler surfaces the detected signature
            vm.Panes.Add(pane);

            // attempt: 1 mirrors the scheduler's FIRST call (it passes loop-index+1 = 1-based).
            await vm.PostThrottleRetryAsync("rl-", attempt: 1);

            var inbox = Path.Combine(root, "inbox");
            var file = Directory.GetFiles(inbox, "rl-*.md").OrderBy(f => f).LastOrDefault();
            Assert.NotNull(file);
            var text = File.ReadAllText(file!);
            Assert.Contains("retry 1: rate-limited", text);   // the scheduler's 1-based attempt, used as-is
            Assert.Contains("429", text);                     // the detected signature rides in the body
            Assert.Contains("watchdog-", text);               // posted as the watchdog, not as an agent
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
