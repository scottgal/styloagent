using Styloagent.Core.Projects;
using Xunit;

public class GitPolicyReaderTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        var p = GitPolicyReader.Read(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Assert.Null(p.TestCommand);
        Assert.True(p.RemoveWorktreeOnMerge);
        Assert.Equal("main", p.MainBranch);
    }

    [Fact]
    public void Reads_values_from_yaml()
    {
        var file = Path.Combine(Path.GetTempPath(), "gitpol-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(file, "testCommand: dotnet test\nremoveWorktreeOnMerge: false\nmainBranch: trunk\n");
        try
        {
            var p = GitPolicyReader.Read(file);
            Assert.Equal("dotnet test", p.TestCommand);
            Assert.False(p.RemoveWorktreeOnMerge);
            Assert.Equal("trunk", p.MainBranch);
        }
        finally { File.Delete(file); }
    }
}
