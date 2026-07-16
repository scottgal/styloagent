using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.Core.Tests;

public class FleetGovernorTests
{
    private static FleetMember M(string prefix, string? parent, int depth)
        => new(prefix, "resp", parent, depth, "running");

    private static FleetState State(int maxFleet, int maxDepth, bool paused, params FleetMember[] members)
        => new(members, maxFleet, maxDepth, paused);

    [Fact]
    public void Allows_a_spawn_under_all_limits()
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Rejects_when_fleet_is_full()
    {
        var members = new List<FleetMember> { M("overview-", null, 0) };
        for (int i = 0; i < 11; i++) members.Add(M($"a{i}-", "overview-", 1));
        var s = new FleetState(members, 12, 3, false);   // 12 members already
        var d = FleetGovernor.Check(s, "overview-", "new-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.FleetFull, d.Reason);
    }

    [Fact]
    public void Rejects_beyond_max_depth()
    {
        // parent at depth 3, MaxDepth 3 → child would be depth 4
        var s = State(12, 3, false, M("overview-", null, 0), M("deep-", "overview-", 3));
        var d = FleetGovernor.Check(s, "deep-", "deeper-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.MaxDepth, d.Reason);
    }

    [Fact]
    public void Rejects_when_paused()
    {
        var s = State(12, 3, true, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.Paused, d.Reason);
    }

    [Fact]
    public void Rejects_duplicate_live_prefix()
    {
        var s = State(12, 3, false, M("overview-", null, 0), M("foss-", "overview-", 1));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.DuplicatePrefix, d.Reason);
    }

    [Fact]
    public void Rejects_unknown_parent()
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "ghost-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.UnknownParent, d.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("no-trailing-dash")]
    [InlineData("Bad Prefix-")]
    public void Rejects_invalid_prefix(string prefix)
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", prefix);
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.InvalidPrefix, d.Reason);
    }

    // ── Re-spawn recovery: a crashed ("exited") member's slot may be reclaimed; a parked
    //    ("dehydrated") member must be rehydrated, not clobbered. ─────────────────────────────
    private static FleetMember M(string prefix, string? parent, int depth, string state)
        => new(prefix, "resp", parent, depth, state);

    [Fact]
    public void Allows_respawn_over_an_exited_prefix()
    {
        var s = State(12, 3, false, M("overview-", null, 0), M("cockpit-", "overview-", 1, "exited"));
        var d = FleetGovernor.Check(s, "overview-", "cockpit-");
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Allows_respawn_over_an_exited_prefix_even_when_fleet_is_full()
    {
        // Reclaiming a dead slot must not trip the fleet-full ceiling — net member count is unchanged.
        var members = new List<FleetMember> { M("overview-", null, 0) };
        for (int i = 0; i < 10; i++) members.Add(M($"a{i}-", "overview-", 1));
        members.Add(M("cockpit-", "overview-", 1, "exited"));   // 12 members, one of them exited
        var s = new FleetState(members, 12, 3, false);
        var d = FleetGovernor.Check(s, "overview-", "cockpit-");
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Rejects_respawn_over_a_dehydrated_prefix_with_a_rehydrate_hint()
    {
        var s = State(12, 3, false, M("overview-", null, 0), M("cockpit-", "overview-", 1, "dehydrated"));
        var d = FleetGovernor.Check(s, "overview-", "cockpit-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.DuplicatePrefix, d.Reason);
        Assert.Contains("rehydrate", d.Message, StringComparison.OrdinalIgnoreCase);
    }
}
