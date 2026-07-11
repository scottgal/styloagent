using Styloagent.Core.Git;
using Xunit;

public class GitStatusParserTests
{
    [Fact]
    public void GitResult_success_and_failure_carry_their_payload()
    {
        var ok = GitResult<int>.Success(5);
        Assert.True(ok.Ok);
        Assert.Equal(5, ok.Value);

        var bad = GitResult.Fail("boom");
        Assert.False(bad.Ok);
        Assert.Equal("boom", bad.Error);
    }

    private const string Sample =
        "# branch.oid abc123\n" +
        "# branch.head agent/foss-\n" +
        "# branch.upstream origin/agent/foss-\n" +
        "# branch.ab +2 -1\n" +
        "1 .M N... 100644 100644 100644 aaa bbb src/Foo.cs\n" +
        "1 A. N... 000000 100644 100644 000 ccc src/Bar.cs\n" +
        "? notes.md\n";

    [Fact]
    public void Parse_reads_ahead_behind_and_changes()
    {
        var s = GitStatusParser.Parse(Sample);
        Assert.True(s.IsDirty);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.False(s.HasConflicts);
        Assert.Equal(3, s.Changes.Count);
        Assert.Contains(s.Changes, c => c.Path == "src/Bar.cs" && c.Kind == GitChangeKind.Added);
        Assert.Contains(s.Changes, c => c.Path == "notes.md" && c.Kind == GitChangeKind.Untracked);
    }

    [Fact]
    public void Parse_flags_conflicts_and_clean()
    {
        var conflict = GitStatusParser.Parse("# branch.ab +0 -0\nu UU N... 1 2 3 4 h1 h2 h3 src/Clash.cs\n");
        Assert.True(conflict.HasConflicts);
        Assert.Contains(conflict.Changes, c => c.Kind == GitChangeKind.Conflicted);

        var clean = GitStatusParser.Parse("# branch.oid abc\n# branch.head main\n# branch.ab +0 -0\n");
        Assert.False(clean.IsDirty);
        Assert.Empty(clean.Changes);
    }

    [Fact]
    public void Parse_renamed_file_uses_the_new_path()
    {
        var s = GitStatusParser.Parse(
            "# branch.ab +0 -0\n" +
            "2 R. N... 100644 100644 100644 ce0136 ce0136 R100 New.cs\tOld.cs\n");
        var change = Assert.Single(s.Changes);
        Assert.Equal("New.cs", change.Path);           // the NEW path, not the original
        Assert.Equal(GitChangeKind.Renamed, change.Kind);
        Assert.True(s.IsDirty);
    }
}
