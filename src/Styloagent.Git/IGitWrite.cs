using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>
/// Narrow seam for mutating the working tree and history via the git CLI.
/// Implemented by <c>GitService</c>. Push establishes an upstream for a new branch when the
/// repository has an origin or a single remote.
/// </summary>
public interface IGitWrite
{
    Task<GitResult> StageAsync(string worktreePath, string relativePath, CancellationToken ct = default);
    Task<GitResult> UnstageAsync(string worktreePath, string relativePath, CancellationToken ct = default);
    Task<GitResult> CommitAsync(string worktreePath, string message, CancellationToken ct = default);
    Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default);
}
