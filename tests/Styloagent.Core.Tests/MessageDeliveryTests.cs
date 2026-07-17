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

    // ---- MCP-native primary path (2026-07-13 design) --------------------------
    // A hook-connected recipient (state != Unknown) is delivered via its own turn-boundary hooks:
    // the note lands in the PendingInbox, NOT the PTY. Injection is kept only for waking an already-idle
    // session and for non-MCP (Unknown) recipients.

    private static PendingInbox TempPending() =>
        new(Path.Combine(Path.GetTempPath(), "styloagent-deliv-tests", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(MessagePriority.Urgent)]  // Interrupt — no longer breaks mid-turn; waits for the Stop boundary
    [InlineData(MessagePriority.Normal)]  // NextPrompt — the recipient's Stop hook delivers when it goes idle
    public async Task Pushing_to_busy_connected_agent_enqueues_push_and_does_not_inject(MessagePriority priority)
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        var action = await svc.DeliverAsync(Msg(priority), "beta-", AgentHookState.Working);

        Assert.Equal(DeliveryAction.EnqueuePending, action);
        Assert.Empty(inj.Calls);                        // no PTY typing
        Assert.True(pending.HasPending("beta-"));
        Assert.Contains("topic", pending.DrainFormatted("beta-"));
        Assert.False(pending.HasPending("beta-"));       // drained
    }

    [Fact]
    public async Task Pushing_to_waiting_for_human_connected_agent_enqueues_push()
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        var action = await svc.DeliverAsync(Msg(MessagePriority.Urgent), "beta-", AgentHookState.WaitingForHuman);

        Assert.Equal(DeliveryAction.EnqueuePending, action);
        Assert.Empty(inj.Calls);
        Assert.True(pending.HasPending("beta-"));
    }

    [Fact]
    public async Task Pushing_to_already_idle_connected_agent_wakes_via_injection_fallback()
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        // No hook will fire on its own for an already-idle session — the injector creates the turn.
        var action = await svc.DeliverAsync(Msg(MessagePriority.Urgent), "beta-", AgentHookState.Idle);

        Assert.Equal(DeliveryAction.Inject, action);
        Assert.Single(inj.Calls);
        Assert.False(inj.Calls[0].BreakFirst);           // idle → plain inject, no ESC-break
        Assert.False(pending.HasPending("beta-"));        // nothing queued for a hook
    }

    [Theory]
    [InlineData(MessagePriority.Low)]   // Convenient — surfaces at next UserPromptSubmit, never forces
    [InlineData(MessagePriority.Info)]  // Informational — shown as context, never actioned
    public async Task Surfacing_to_connected_agent_enqueues_info_never_injects(MessagePriority priority)
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        var action = await svc.DeliverAsync(Msg(priority), "beta-", AgentHookState.Working);

        Assert.Equal(DeliveryAction.EnqueuePending, action);
        Assert.Empty(inj.Calls);
        Assert.True(pending.HasPending("beta-"));
    }

    [Fact]
    public async Task Exited_connected_recipient_gets_nothing()
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        var action = await svc.DeliverAsync(Msg(MessagePriority.Urgent), "beta-", AgentHookState.Exited);

        Assert.Equal(DeliveryAction.None, action);
        Assert.Empty(inj.Calls);
        Assert.False(pending.HasPending("beta-"));
    }

    // ---- picked-up signal (bus viewer "being-worked-on" pill) -----------------
    // The message's FilePath is what the viewer keys pickup by (via its RoutingPrefix == recipient prefix).

    [Fact]
    public async Task Queued_to_busy_connected_agent_is_not_picked_up_until_the_note_drains()
    {
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, new FakeInjector(), pending);
        var msg = Msg(MessagePriority.Urgent);

        await svc.DeliverAsync(msg, "beta-", AgentHookState.Working);   // enqueues push, records delivered
        Assert.False(pending.PickedUp("beta-", msg.FilePath));          // still waiting

        pending.DrainFormatted("beta-");                                // recipient's Stop hook drains it
        Assert.True(pending.PickedUp("beta-", msg.FilePath));           // → being worked on
    }

    [Fact]
    public async Task Injected_to_idle_connected_agent_is_picked_up_immediately()
    {
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, new FakeInjector(), pending);
        var msg = Msg(MessagePriority.Urgent);

        // Idle → injected (a fresh turn), never queued → nothing left pending → picked up right away.
        var action = await svc.DeliverAsync(msg, "beta-", AgentHookState.Idle);
        Assert.Equal(DeliveryAction.Inject, action);
        Assert.True(pending.PickedUp("beta-", msg.FilePath));
    }

    [Fact]
    public async Task Unknown_recipient_falls_back_to_injection_even_with_pending_configured()
    {
        var inj = new FakeInjector();
        var pending = TempPending();
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj, pending);

        // Hooks not yet wired (just spawned) → the injector is the safety net, and nothing is queued for a
        // hook that may never drain.
        var action = await svc.DeliverAsync(Msg(MessagePriority.Urgent), "beta-", AgentHookState.Unknown);

        Assert.Equal(DeliveryAction.Inject, action);
        Assert.Single(inj.Calls);
        Assert.False(pending.HasPending("beta-"));
    }
}
