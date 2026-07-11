using Styloagent.Core.Router;
using Xunit;

public class RouterResolverTests
{
    [Fact]
    public void RouterDecision_carries_a_grant()
    {
        var d = new RouterDecision(RouterAction.Grant, "prod", ResourceKind.Account, "deploy", "foss-",
            new System.DateTimeOffset(2026, 7, 11, 12, 0, 0, System.TimeSpan.Zero));
        Assert.Equal(RouterAction.Grant, d.Action);
        Assert.Equal("deploy", d.Name);
        Assert.Equal("foss-", d.Prefix);
    }

    private static DateTimeOffset T(int sec) => new(2026, 7, 11, 12, 0, sec, TimeSpan.Zero);

    private static ResourceState Account(string name, int capacity,
        Claim[] claims, Grant[] grants) =>
        new("prod", ResourceKind.Account, name, new ResourcePolicy(capacity, null, TimeSpan.FromMinutes(2)),
            claims, grants, System.Array.Empty<AttemptLine>());

    [Fact]
    public void Grants_the_earliest_claim_up_to_capacity()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("docs-", T(5), "x"), new Claim("foss-", T(3), "y") },
            grants: System.Array.Empty<Grant>());
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));

        var grant = Assert.Single(decisions);
        Assert.Equal(RouterAction.Grant, grant.Action);
        Assert.Equal("foss-", grant.Prefix);           // earlier timestamp wins
    }

    [Fact]
    public void Capacity_N_grants_N_and_queues_the_rest()
    {
        var r = Account("ci", capacity: 2,
            claims: new[] { new Claim("a-", T(1), ""), new Claim("b-", T(2), ""), new Claim("c-", T(3), "") },
            grants: System.Array.Empty<Grant>());
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, d => d.Prefix == "a-");
        Assert.Contains(decisions, d => d.Prefix == "b-");
        Assert.DoesNotContain(decisions, d => d.Prefix == "c-");   // queued
    }

    [Fact]
    public void Full_capacity_grants_nothing()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("b-", T(5), "") },
            grants: new[] { new Grant("a-", T(1), T(9), T(0)) });   // held, heartbeat recent
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Empty(decisions);
    }

    [Fact]
    public void Expired_grant_is_expired_and_the_queue_head_promoted()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("b-", T(5), "") },
            grants: new[] { new Grant("a-", T(1), T(1), T(0)) });   // heartbeat at T(1)
        // leaseTtl = 2m; at T(200) => 199s since heartbeat >= 120s => expired
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }),
            new DateTimeOffset(2026, 7, 11, 12, 3, 20, TimeSpan.Zero));

        Assert.Contains(decisions, d => d.Action == RouterAction.Expire && d.Prefix == "a-");
        Assert.Contains(decisions, d => d.Action == RouterAction.Grant && d.Prefix == "b-");
    }

    [Fact]
    public void Live_grant_is_not_expired()
    {
        var r = Account("deploy", capacity: 1,
            claims: System.Array.Empty<Claim>(),
            grants: new[] { new Grant("a-", T(1), T(9), T(0)) });   // heartbeat T(9), now T(10)
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Empty(decisions);
    }
}
