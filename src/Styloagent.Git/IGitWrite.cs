using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>
/// Narrow seam for mutating the working tree and history via the git CLI.
/// Stage/Unstage/Commit are implemented in <c>GitService</c>.
/// Push and Pull are declared here for interface stability and implemented in Task 3.
/// </summary>
public interface IGitWrite
{
    Task<GitResult> StageAsync(string worktreePath, string relativePath, CancellationToken ct = default);
    Task<GitResult> UnstageAsync(string worktreePath, string relativePath, CancellationToken ct = default);
    Task<GitResult> CommitAsync(string worktreePath, string message, CancellationToken ct = default);
    Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default);
}
