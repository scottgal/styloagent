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
}
