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
        Assert.Contains("awaiting reply", md);
        Assert.Contains("mae- -->".Replace("mae-", "mae"), md);   // edge from mae to the awaiting node
    }

    [Fact]
    public void Build_is_empty_safe()
    {
        var md = BusSequenceGenerator.Build(Array.Empty<SeqThread>());
        Assert.Contains("graph LR", md);
        Assert.Contains("no bus activity yet", md);
    }
}
