using Styloagent.Core.Attention;
using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;
using Xunit;

public class PickupProjectionTests
{
    private static PendingInbox TempPending() =>
        new(Path.Combine(Path.GetTempPath(), "styloagent-pickup-tests", Guid.NewGuid().ToString("N")));

    // The viewer keys pickup by the message's own (FilePath, RoutingPrefix): RoutingPrefix is the recipient
    // prefix the note was delivered under, so a row looks its own status up with no extra plumbing.
    private static BusMessage Msg() =>
        new("topic", "beta-", BusMessageKind.Inbox, BusMessageState.New,
            "/ch/inbox/beta-topic.md", null, "ops", "body", MessagePriority.Urgent);

    [Fact]
    public async Task Not_picked_up_while_the_note_is_still_pending()
    {
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, new NoopInjector(), pending);
        var projection = new PickupProjection(pending);
        var msg = Msg();

        await svc.DeliverAsync(msg, "beta-", AgentHookState.Working);

        Assert.False(projection.IsPickedUp(msg.FilePath, msg.RoutingPrefix));
    }

    [Fact]
    public async Task Picked_up_once_the_recipient_drains_the_note()
    {
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, new NoopInjector(), pending);
        var projection = new PickupProjection(pending);
        var msg = Msg();

        await svc.DeliverAsync(msg, "beta-", AgentHookState.Working);
        pending.DrainFormatted("beta-");

        Assert.True(projection.IsPickedUp(msg.FilePath, msg.RoutingPrefix));
    }

    [Fact]
    public void Never_delivered_message_is_not_picked_up()
    {
        var projection = new PickupProjection(TempPending());
        var msg = Msg();
        Assert.False(projection.IsPickedUp(msg.FilePath, msg.RoutingPrefix));
    }

    [Fact]
    public void A_null_pending_store_reads_as_not_picked_up()
    {
        // Delivery not MCP-wired → the viewer simply shows WAITING/DONE as it does today, no pickup pill.
        var projection = new PickupProjection(null);
        var msg = Msg();
        Assert.False(projection.IsPickedUp(msg.FilePath, msg.RoutingPrefix));
    }

    private sealed class NoopInjector : IMessageInjector
    {
        public Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
