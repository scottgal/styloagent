using Styloagent.Core.Diagrams;
using Xunit;

namespace Styloagent.Core.Tests;

public class SystemMapGeneratorTests
{
    [Fact]
    public void Build_emits_a_flowchart_with_nodes_and_parent_edges()
    {
        var md = SystemMapGenerator.Build(new[]
        {
            new FleetNode("overview-", null, "the architect", "working"),
            new FleetNode("foss-", "overview-", "owns FOSS", "needs you"),
        });

        Assert.Contains("```mermaid", md);
        Assert.Contains("graph TD", md);
        Assert.Contains("overview-<br/>the architect", md);       // node label
        Assert.Contains("foss-<br/>owns FOSS", md);
        Assert.Contains("--> ", md);                              // an edge exists
        Assert.Contains("class ", md);                            // state styling applied
    }

    [Fact]
    public void Build_is_empty_safe()
    {
        var md = SystemMapGenerator.Build(Array.Empty<FleetNode>());
        Assert.Contains("graph TD", md);
        Assert.Contains("no agents yet", md);
    }

    [Fact]
    public void Build_sanitizes_ids_but_keeps_prefix_in_the_label()
    {
        var md = SystemMapGenerator.Build(new[] { new FleetNode("foss-", null, "r", "working") });
        Assert.Contains("foss[\"foss-<br/>r\"]", md);             // id 'foss', label 'foss-'
    }
}
