using Styloagent.Core.Diagrams;
using Xunit;

namespace Styloagent.Core.Tests;

public class BusSequenceGeneratorTests
{
    private static SeqMessage M(string from, int minAgo)
        => new(from, DateTimeOffset.UtcNow.AddMinutes(-minAgo));

    [Fact]
    public void Build_emits_a_flowchart_edge_between_consecutive_senders()
    {
        var md = BusSequenceGenerator.Build(new[]
        {
            new SeqThread("release-cut", new[] { M("deploy-", 5), M("foss-", 4) }),
        });

        Assert.Contains("```mermaid", md);
        Assert.Contains("graph LR", md);
        Assert.Contains("deploy[\"deploy-\"]", md);
        Assert.Contains("foss[\"foss-\"]", md);
        Assert.Contains("deploy -->|release-cut| foss", md);
    }

    [Fact]
    public void Single_sender_thread_shows_awaiting()
    {
        var md = BusSequenceGenerator.Build(new[]
        {
            new SeqThread("ping", new[] { M("mae-", 2) }),
        });
        // Awaiting node exists with the thread slug and sender name
        Assert.Contains("awaiting reply", md);
        Assert.Contains("ping", md);
        // Edge from sender (mae-) to awaiting node (await0) exists
        Assert.Contains("mae --> await0", md);
    }

    [Fact]
    public void Build_handles_a_returning_sender()
    {
        // Thread with three messages: A (3 min ago), B (2 min ago), A (1 min ago)
        // Ordered by When ascending: a (1 min), b (2 min), a (3 min) — oldest first
        // The chain builder will see: a, b, a (since oldest message is from a)
        // Wait, let me verify: OrderBy(m => m.When) with AddMinutes(-X) means smaller X = more recent
        // So M("a-", 3) is 3 min ago, M("b-", 2) is 2 min ago, M("a-", 1) is 1 min ago (most recent)
        // OrderBy(m.When) gives oldest first: a (3 min ago), b (2 min ago), a (1 min ago)
        var md = BusSequenceGenerator.Build(new[]
        {
            new SeqThread("ping", new[] { M("a-", 3), M("b-", 2), M("a-", 1) }),
        });
        // Two edges should exist: a --> b and b --> a
        Assert.Contains("a -->|ping| b", md);
        Assert.Contains("b -->|ping| a", md);
    }

    [Fact]
    public void Build_is_empty_safe()
    {
        var md = BusSequenceGenerator.Build(Array.Empty<SeqThread>());
        Assert.Contains("graph LR", md);
        Assert.Contains("no bus activity yet", md);
    }
}
