using Styloagent.Core.Channel;
using Xunit;

public class MessageRoutingTests
{
    private static readonly string[] Live = { "alpha-", "beta-", "overview-" };

    private static BusMessage Msg(string routingPrefix, BusMessageKind kind) =>
        new("slug", routingPrefix, kind, BusMessageState.New, "/ch/x.md", null, "ops", "body");

    [Fact]
    public void Addressed_message_routes_to_the_matching_agent()
    {
        var r = MessageRouting.RecipientsFor(Msg("beta-", BusMessageKind.Inbox), Live);
        Assert.Equal("beta-", Assert.Single(r));
    }

    [Fact]
    public void FollowUp_routes_like_inbox()
    {
        var r = MessageRouting.RecipientsFor(Msg("alpha-", BusMessageKind.FollowUp), Live);
        Assert.Equal("alpha-", Assert.Single(r));
    }

    [Fact]
    public void Addressee_not_live_yields_no_recipients()
    {
        var r = MessageRouting.RecipientsFor(Msg("gamma-", BusMessageKind.Inbox), Live);
        Assert.Empty(r);
    }

    [Fact]
    public void Broadcast_routes_to_everyone_live()
    {
        var r = MessageRouting.RecipientsFor(Msg("all-", BusMessageKind.Broadcast), Live);
        Assert.Equal(Live, r);
    }

    [Theory]
    [InlineData(BusMessageKind.Reply)]
    [InlineData(BusMessageKind.BroadcastReply)]
    public void Replies_do_not_route(BusMessageKind kind)
    {
        var r = MessageRouting.RecipientsFor(Msg("beta-", kind), Live);
        Assert.Empty(r);
    }
}
