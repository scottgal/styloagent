using Styloagent.Git;
using Xunit;

public class CommitLogParserTests
{
    [Fact]
    public void Parse_reads_sha_parents_and_subject()
    {
        // fields: SHA \0 Parents \0 Decorators \0 author±email \0 authortime \0 committer±email \0 committertime \0 subject
        // Line 1: merge commit with 2 parents
        // Line 2: root commit with no parents (two consecutive \0)
        var sep = "\0";
        var log =
            "aaa" + sep + "bbb ccc" + sep + "HEAD -> refs/heads/main" + sep + "Ann±a@x.com" + sep + "1700000000" + sep + "Ann±a@x.com" + sep + "1700000000" + sep + "top\n" +
            "bbb" + sep + "" + sep + "" + sep + "Bob±b@x.com" + sep + "1699000000" + sep + "Bob±b@x.com" + sep + "1699000000" + sep + "init\n";

        var commits = CommitLogParser.Parse(log);

        Assert.Equal(2, commits.Count);

        Assert.Equal("aaa", commits[0].SHA);
        Assert.Equal(2, commits[0].Parents.Count);
        Assert.Contains("bbb", commits[0].Parents);
        Assert.Contains("ccc", commits[0].Parents);
        Assert.Equal("top", commits[0].Subject);
        Assert.Equal("Ann", commits[0].Author.Name);
        Assert.Equal("a@x.com", commits[0].Author.Email);
        Assert.Equal(1700000000UL, commits[0].CommitterTime);

        Assert.Equal("bbb", commits[1].SHA);
        Assert.Empty(commits[1].Parents);
        Assert.Equal("init", commits[1].Subject);
    }
}
