using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;
using Xunit;

public class MessageDeliveryTests
{
    private static BusMessage Msg(MessagePriority priority) =>
        new("topic", "alpha-", BusMessageKind.Inbox, BusMessageState.New,
            "/ch/inbox/alpha-topic.md", null, "ops", "body", priority);

    // ---- pure decision matrix -------------------------------------------------

    [Theory]
    [InlineData(DeliveryMode.Interrupt, AgentHookState.Working, DeliveryAction.InjectWithBreak)]
    [InlineData(DeliveryMode.Interrupt, AgentHookState.Idle, DeliveryAction.Inject)]
    [InlineData(DeliveryMode.Interrupt, AgentHookState.WaitingForHuman, DeliveryAction.Inject)]
    [InlineData(DeliveryMode.Interrupt, AgentHookState.Exited, DeliveryAction.None)]
    [InlineData(DeliveryMode.NextPrompt, AgentHookState.Idle, DeliveryAction.Inject)]
    [InlineData(DeliveryMode.NextPrompt, AgentHookState.Working, DeliveryAction.DeferUntilIdle)]
    [InlineData(DeliveryMode.NextPrompt, AgentHookState.WaitingForHuman, DeliveryAction.DeferUntilIdle)]
    [InlineData(DeliveryMode.NextPrompt, AgentHookState.Exited, DeliveryAction.None)]
    [InlineData(DeliveryMode.Poll, AgentHookState.Idle, DeliveryAction.None)]
    [InlineData(DeliveryMode.Convenient, AgentHookState.Working, DeliveryAction.None)]
    [InlineData(DeliveryMode.Informational, AgentHookState.Idle, DeliveryAction.None)]
    public void Decide_matrix(DeliveryMode mode, AgentHookState state, DeliveryAction expected)
        => Assert.Equal(expected, MessageDelivery.Decide(mode, state));

    [Fact]
    public void FormatNudge_includes_slug_from_and_path()
    {
        var nudge = MessageDelivery.FormatNudge(Msg(MessagePriority.Urgent));
        Assert.Contains("urgent", nudge);
        Assert.Contains("topic", nudge);
        Assert.Contains("ops", nudge);
        Assert.Contains("/ch/inbox/alpha-topic.md", nudge);
    }

    // ---- service (deferral queue) ---------------------------------------------

    private sealed record Injection(string AgentId, string Text, bool BreakFirst);

    private sealed class FakeInjector : IMessageInjector
    {
        public readonly List<Injection> Calls = new();
        public Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
        {
            Calls.Add(new Injection(agentId, text, breakFirst));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Urgent_to_working_agent_injects_with_break_immediately()
    {
        var inj = new FakeInjector();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj);

        var action = await svc.DeliverAsync(Msg(MessagePriority.Urgent), "beta", AgentHookState.Working);

        Assert.Equal(DeliveryAction.InjectWithBreak, action);
        Assert.Single(inj.Calls);
        Assert.True(inj.Calls[0].BreakFirst);
        Assert.Equal("beta", inj.Calls[0].AgentId);
    }

    [Fact]
    public async Task Normal_to_busy_agent_defers_then_injects_on_idle()
    {
        var inj = new FakeInjector();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj);

        // normal -> NextPrompt; agent busy -> defer, nothing injected yet
        var action = await svc.DeliverAsync(Msg(MessagePriority.Normal), "beta", AgentHookState.Working);
        Assert.Equal(DeliveryAction.DeferUntilIdle, action);
        Assert.Empty(inj.Calls);
        Assert.Equal(1, svc.DeferredCount("beta"));

        // a non-idle transition does not flush
        await svc.OnRecipientStateChangedAsync("beta", AgentHookState.WaitingForHuman);
        Assert.Empty(inj.Calls);

        // going idle flushes the queue (no break)
        await svc.OnRecipientStateChangedAsync("beta", AgentHookState.Idle);
        Assert.Single(inj.Calls);
        Assert.False(inj.Calls[0].BreakFirst);
        Assert.Equal(0, svc.DeferredCount("beta"));
    }

    [Fact]
    public async Task Info_message_injects_nothing()
    {
        var inj = new FakeInjector();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj);

        var action = await svc.DeliverAsync(Msg(MessagePriority.Info), "beta", AgentHookState.Idle);

        Assert.Equal(DeliveryAction.None, action);
        Assert.Empty(inj.Calls);
    }
}
