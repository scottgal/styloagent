using Styloagent.Core.Attention;
using Xunit;

namespace Styloagent.Core.Tests;

public class AttentionModelTests
{
    private static AttentionCandidate C(string id, bool needs, int? waitMinutesAgo)
        => new(id, needs, waitMinutesAgo is null ? null : DateTimeOffset.UtcNow.AddMinutes(-waitMinutesAgo.Value));

    private static readonly string[] ExpectedOldestFirst = ["old-", "mid-", "young-"];
    private static readonly string[] ExpectedTimedFirst = ["timed-", "nulltime-"];

    [Fact]
    public void Build_orders_waiting_oldest_first_and_excludes_non_waiting()
    {
        var q = AttentionQueue.Build(new[]
        {
            C("young-", true, 1),
            C("working-", false, null),
            C("old-", true, 10),
            C("mid-", true, 5),
        });
        Assert.Equal(ExpectedOldestFirst, q);   // oldest-first, working- excluded
    }

    [Fact]
    public void Build_puts_null_waiting_since_last()
    {
        var q = AttentionQueue.Build(new[]
        {
            C("nulltime-", true, null),
            C("timed-", true, 3),
        });
        Assert.Equal(ExpectedTimedFirst, q);
    }

    [Fact]
    public void Build_is_empty_when_none_waiting()
        => Assert.Empty(AttentionQueue.Build(new[] { C("a-", false, null), C("b-", false, 2) }));

    [Theory]
    [InlineData(false, "old-", "dash-", "old-")]   // idle, head != active → reveal head
    [InlineData(true,  "old-", "dash-", null)]     // busy → no reveal
    [InlineData(false, "old-", "old-", null)]      // head already active → no reveal
    [InlineData(false, null,   "dash-", null)]     // empty queue → no reveal
    public void Decide_reveals_only_when_idle_and_head_not_active(bool busy, string? head, string? active, string? expected)
        => Assert.Equal(expected, AutoReveal.Decide(busy, head, active));
}
