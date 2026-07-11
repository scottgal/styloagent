using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

public class UnifiedDiffParserTests
{
    [Fact]
    public void FileDiff_empty_has_no_lines()
    {
        var d = FileDiff.Empty("src/Foo.cs");
        Assert.Equal("src/Foo.cs", d.Path);
        Assert.Empty(d.Lines);
        Assert.False(d.IsBinary);
    }

    private const string Sample =
        "diff --git a/Foo.cs b/Foo.cs\n" +
        "index 111..222 100644\n" +
        "--- a/Foo.cs\n" +
        "+++ b/Foo.cs\n" +
        "@@ -1,3 +1,3 @@\n" +
        " unchanged\n" +
        "-old line\n" +
        "+new line\n" +
        " tail\n";

    [Fact]
    public void Parse_classifies_added_deleted_and_context()
    {
        var d = UnifiedDiffParser.Parse("Foo.cs", Sample);
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Deleted);
        Assert.False(d.IsBinary);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Added && l.Content == "new line" && l.NewLine == 2);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Deleted && l.Content == "old line" && l.OldLine == 2);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Header);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Context && l.Content == "unchanged");
    }

    [Fact]
    public void Parse_flags_binary()
    {
        var d = UnifiedDiffParser.Parse("img.png", "diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n");
        Assert.True(d.IsBinary);
    }
}
