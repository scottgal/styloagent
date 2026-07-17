using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

/// <summary>
/// The context-usage monitor that decides WHEN an agent should be nudged to checkpoint (commit WIP +
/// refresh its resume doc) BEFORE a hard compaction. It is a pure state machine: fed a stream of per-agent
/// context-fill fractions, it fires ONCE per approach to the soft threshold and only re-arms after a
/// compaction has shrunk the fill back down (hysteresis) — so it nudges at each fill-up, never every tick.
/// It DECIDES; the send is wired by the App/bus seam.
/// </summary>
public class ContextCheckpointMonitorTests
{
    [Fact]
    public void Crossing_the_soft_threshold_fires_once()
    {
        var monitor = new ContextCheckpointMonitor(threshold: 0.80);
        var fired = new List<string>();
        monitor.CheckpointNeeded += fired.Add;

        Assert.True(monitor.Observe("session-", 0.85));

        Assert.Equal("session-", Assert.Single(fired));
    }

    [Fact]
    public void Below_the_threshold_never_fires()
    {
        var monitor = new ContextCheckpointMonitor(threshold: 0.80);
        Assert.False(monitor.Observe("session-", 0.10));
        Assert.False(monitor.Observe("session-", 0.79));
    }

    [Fact]
    public void Staying_above_the_threshold_does_not_re_fire_every_tick()
    {
        var monitor = new ContextCheckpointMonitor(threshold: 0.80);
        int count = 0;
        monitor.CheckpointNeeded += _ => count++;

        Assert.True(monitor.Observe("session-", 0.85));    // crosses → fire
        Assert.False(monitor.Observe("session-", 0.90));   // still high → no re-fire
        Assert.False(monitor.Observe("session-", 0.99));   // still high → no re-fire

        Assert.Equal(1, count);
    }

    [Fact]
    public void Re_arms_only_after_the_fill_drops_below_the_hysteresis_band()
    {
        // threshold 0.80, hysteresis 0.10 → re-arm only after fill falls below 0.70.
        var monitor = new ContextCheckpointMonitor(threshold: 0.80, rearmHysteresis: 0.10);
        int count = 0;
        monitor.CheckpointNeeded += _ => count++;

        Assert.True(monitor.Observe("session-", 0.85));    // fire
        Assert.False(monitor.Observe("session-", 0.72));   // dipped but still inside the band → NOT re-armed
        Assert.False(monitor.Observe("session-", 0.90));   // back up, but never re-armed → no re-fire

        Assert.Equal(1, count);
    }

    [Fact]
    public void Fires_again_after_a_compaction_shrinks_the_context()
    {
        // The real cycle: fill up → nudge → agent checkpoints → compaction drops fill → fills up again → nudge.
        var monitor = new ContextCheckpointMonitor(threshold: 0.80, rearmHysteresis: 0.10);
        int count = 0;
        monitor.CheckpointNeeded += _ => count++;

        Assert.True(monitor.Observe("session-", 0.82));    // first fill-up → fire
        Assert.False(monitor.Observe("session-", 0.35));   // compaction shrank it (below 0.70) → re-arm
        Assert.True(monitor.Observe("session-", 0.83));    // second fill-up → fire again

        Assert.Equal(2, count);
    }

    [Fact]
    public void Tracks_each_agent_independently()
    {
        var monitor = new ContextCheckpointMonitor(threshold: 0.80);
        var fired = new List<string>();
        monitor.CheckpointNeeded += fired.Add;

        Assert.True(monitor.Observe("session-", 0.85));    // session- fires
        Assert.False(monitor.Observe("bus-", 0.40));       // bus- low → nothing
        Assert.False(monitor.Observe("session-", 0.88));   // session- already fired
        Assert.True(monitor.Observe("bus-", 0.90));        // bus- crosses → fires

        Assert.Collection(fired,
            x => Assert.Equal("session-", x),
            x => Assert.Equal("bus-", x));
    }

    [Fact]
    public void The_threshold_is_configurable()
    {
        var monitor = new ContextCheckpointMonitor(threshold: 0.50);
        Assert.True(monitor.Observe("session-", 0.55));
    }

    [Fact]
    public void A_blank_prefix_is_ignored_and_never_fires()
    {
        // Degrade-never-destroy: a missing prefix (no session id yet) must never crash or fire a nudge to nobody.
        var monitor = new ContextCheckpointMonitor(threshold: 0.80);
        Assert.False(monitor.Observe("", 0.99));
        Assert.False(monitor.Observe("   ", 0.99));
        Assert.False(monitor.Observe(null!, 0.99));
    }
}
