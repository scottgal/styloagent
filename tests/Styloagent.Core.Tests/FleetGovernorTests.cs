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
}
