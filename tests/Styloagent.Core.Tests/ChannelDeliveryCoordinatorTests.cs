using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;
using Xunit;

public class ChannelDeliveryCoordinatorTests
{
    private static readonly IReadOnlyCollection<string> Prefixes = new[] { "alpha-", "beta-" };

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

    private static string NewChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "deliv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        return root;
    }

    private static void Drop(string root, string file, string header) =>
        File.WriteAllText(Path.Combine(root, "inbox", file), header + "\n\nBody.");

    private static ChannelDeliveryCoordinator Coordinator(
        string root, FakeInjector inj, params AgentPresence[] agents)
    {
        var svc = new MessageDeliveryService(PriorityPolicy.Default, inj);
        return new ChannelDeliveryCoordinator(root, Prefixes, svc, () => agents);
    }

    [Fact]
    public async Task Urgent_to_working_agent_injects_with_break()
    {
        var root = NewChannel();
        try
        {
            var inj = new FakeInjector();
            var coord = Coordinator(root, inj, new AgentPresence("beta-", AgentHookState.Working));

            Drop(root, "beta-broken-build.md", "**From:** alpha-\n**Priority:** urgent");
            var n = await coord.PumpAsync();

            Assert.Equal(1, n);
            var call = Assert.Single(inj.Calls);
            Assert.Equal("beta-", call.AgentId);
            Assert.True(call.BreakFirst);                    // urgent + working -> ESC break
            Assert.Contains("broken-build", call.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Normal_to_idle_agent_injects_without_break()
    {
        var root = NewChannel();
        try
        {
            var inj = new FakeInjector();
            var coord = Coordinator(root, inj, new AgentPresence("beta-", AgentHookState.Idle));

            Drop(root, "beta-fyi.md", "**From:** alpha-");   // no priority -> normal
            await coord.PumpAsync();

            var call = Assert.Single(inj.Calls);
            Assert.False(call.BreakFirst);                   // normal + idle -> inject, no break
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Each_message_delivered_once_across_pumps()
    {
        var root = NewChannel();
        try
        {
            var inj = new FakeInjector();
            var coord = Coordinator(root, inj, new AgentPresence("beta-", AgentHookState.Idle));

            Drop(root, "beta-one.md", "**From:** alpha-\n**Priority:** urgent");
            await coord.PumpAsync();
            await coord.PumpAsync();                          // same message, second pump

            Assert.Single(inj.Calls);                         // not re-delivered
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Seed_suppresses_preexisting_backlog()
    {
        var root = NewChannel();
        try
        {
            var inj = new FakeInjector();
            var coord = Coordinator(root, inj, new AgentPresence("beta-", AgentHookState.Working));

            Drop(root, "beta-old.md", "**From:** alpha-\n**Priority:** urgent");
            await coord.SeedAsync();                           // pre-existing -> marked seen
            await coord.PumpAsync();

            Assert.Empty(inj.Calls);                           // backlog not delivered

            // ...but a message that arrives after seeding IS delivered.
            Drop(root, "beta-new.md", "**From:** alpha-\n**Priority:** urgent");
            await coord.PumpAsync();
            Assert.Single(inj.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Info_message_delivers_nothing()
    {
        var root = NewChannel();
        try
        {
            var inj = new FakeInjector();
            var coord = Coordinator(root, inj, new AgentPresence("beta-", AgentHookState.Idle));

            Drop(root, "beta-note.md", "**From:** alpha-\n**Priority:** info");
            await coord.PumpAsync();

            Assert.Empty(inj.Calls);                           // informational -> HUD only
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
