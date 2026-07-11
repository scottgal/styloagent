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
}
