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
}
