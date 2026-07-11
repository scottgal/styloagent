using Styloagent.Core.Git;
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
}
