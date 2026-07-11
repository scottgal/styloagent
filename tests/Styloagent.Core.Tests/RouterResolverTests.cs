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
}
