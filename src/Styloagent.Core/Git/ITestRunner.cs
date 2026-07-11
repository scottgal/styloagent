namespace Styloagent.Core.Git;

/// <summary>Result of running a project's test command before wrap-up.</summary>
public sealed record TestOutcome(bool Passed, string Output);

/// <summary>Runs a project's configured test command in a worktree. Faked in tests.</summary>
public interface ITestRunner
{
    Task<TestOutcome> RunAsync(string workingDir, string command, CancellationToken ct = default);
}
