using System.Collections.Generic;
using Styloagent.Git.Vendored.Models;
using Xunit;

public class CommitGraphGenerateTests
{
    [Fact]
    public void Commit_parses_parents_from_space_separated_shas()
    {
        var c = new Commit { SHA = "aaa" };
        c.ParseParents("bbb ccc");
        Assert.Equal(2, c.Parents.Count);
        Assert.Contains("bbb", c.Parents);
    }

    [Fact]
    public void Generate_builds_a_graph_from_a_linear_history()
    {
        CommitGraph.SetDefaultPens();
        var commits = new List<Commit>
        {
            new() { SHA = "c", Parents = { "b" }, Color = 0 },
            new() { SHA = "b", Parents = { "a" }, Color = 0 },
            new() { SHA = "a", Color = 0 },
        };
        var graph = CommitGraph.Generate(commits, recalculateMergeState: false,
            firstParentOnlyEnabled: false, CommitGraphHighlighting.All, new HashSet<string>());
        Assert.NotNull(graph);
    }
}
