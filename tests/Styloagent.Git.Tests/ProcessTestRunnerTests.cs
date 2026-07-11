using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

public class ProcessTestRunnerTests
{
    [Fact]
    public async Task Passing_command_reports_passed()
    {
        var r = new ProcessTestRunner();
        var outcome = await r.RunAsync(Path.GetTempPath(), "exit 0");
        Assert.True(outcome.Passed);
    }

    [Fact]
    public async Task Failing_command_reports_not_passed_with_output()
    {
        var r = new ProcessTestRunner();
        var outcome = await r.RunAsync(Path.GetTempPath(), "echo nope 1>&2; exit 1");
        Assert.False(outcome.Passed);
        Assert.Contains("nope", outcome.Output);
    }
}
