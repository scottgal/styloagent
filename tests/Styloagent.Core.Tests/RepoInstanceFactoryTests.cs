using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;

namespace Styloagent.Core.Tests;

/// <summary>
/// Bug A piece 2 — the per-repo delivery coordinator factory. Each federated instance gets its OWN delivery
/// stack (channel + PendingInbox/hooksDir + delivery service + coordinator) bound to its repo's channel, so
/// the 2nd instance delivers independently. Proves the Model A key: <c>(repoRoot, prefix)</c> — the same
/// prefix in two repos never collides because each coordinator reads only its own channelRoot.
/// </summary>
public class RepoInstanceFactoryTests
{
    private sealed class RecordingInjector : IMessageInjector
    {
        public List<(string Agent, string Text)> Injected { get; } = new();
        public Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
        {
            Injected.Add((agentId, text));
            return Task.CompletedTask;
        }
    }

    private static string TempRepo(params string[] prefixes)
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-repoinst-" + Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(root, ".styloagent", "channel", "saved-context");
        Directory.CreateDirectory(savedContext);
        foreach (var p in prefixes)
            File.WriteAllText(Path.Combine(savedContext, $"{p}context.md"), "ctx");
        return root;
    }

    private static Func<IReadOnlyList<AgentPresence>> IdleAgent(string prefix)
    {
        IReadOnlyList<AgentPresence> live = new List<AgentPresence> { new(prefix, AgentHookState.Idle) };
        return () => live;
    }

    private static void Drop(string channelRoot, string to, string subject) =>
        ChannelMessageWriter.Write(channelRoot, from: "overview-", to: to, subject: subject,
            body: "x", priority: "normal", timestamp: DateTimeOffset.Now);

    [Fact]
    public async Task Factory_builds_a_coordinator_bound_to_the_repos_own_channel()
    {
        var repo = TempRepo("session-");
        try
        {
            var injector = new RecordingInjector();
            var instance = await new RepoInstanceFactory().CreateAsync(
                repo, hooksDirectory: Path.Combine(repo, ".hooks"),
                PriorityPolicy.Default, injector, IdleAgent("session-"));

            Assert.Equal(repo, instance.Channel.RepoRoot);
            Assert.Equal(RepoChannelResolver.ChannelRootFor(repo), instance.Channel.ChannelRoot);

            await instance.Coordinator.SeedAsync();               // clear the (empty) backlog
            Drop(instance.Channel.ChannelRoot, "session-", "hi there");
            var delivered = await instance.Coordinator.PumpAsync();

            Assert.Equal(1, delivered);
            var got = Assert.Single(injector.Injected);
            Assert.Equal("session-", got.Agent);
            Assert.Contains("hi-there", got.Text);                // nudge points at this repo's message file
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task Same_prefix_in_two_repos_delivers_independently_repoRoot_is_the_key()
    {
        var repoA = TempRepo("session-");
        var repoB = TempRepo("session-");
        try
        {
            var injA = new RecordingInjector();
            var injB = new RecordingInjector();
            var factory = new RepoInstanceFactory();
            var a = await factory.CreateAsync(repoA, Path.Combine(repoA, ".hooks"), PriorityPolicy.Default, injA, IdleAgent("session-"));
            var b = await factory.CreateAsync(repoB, Path.Combine(repoB, ".hooks"), PriorityPolicy.Default, injB, IdleAgent("session-"));
            await a.Coordinator.SeedAsync();
            await b.Coordinator.SeedAsync();

            // A cross-repo send physically writes into the TARGET repo's channel — here, repo A's.
            Drop(a.Channel.ChannelRoot, "session-", "for A only");
            await a.Coordinator.PumpAsync();
            await b.Coordinator.PumpAsync();

            Assert.Single(injA.Injected);   // repo A's session- received it
            Assert.Empty(injB.Injected);    // repo B's session- did NOT — same prefix, different repoRoot
        }
        finally { Directory.Delete(repoA, recursive: true); Directory.Delete(repoB, recursive: true); }
    }
}
