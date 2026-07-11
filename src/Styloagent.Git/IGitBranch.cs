using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>
/// Narrow seam for local-branch operations: enumerate, create, and switch.
/// Implemented in <c>GitService</c>. Never throws across the seam.
/// </summary>
public interface IGitBranch
{
    Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> CreateBranchAsync(string worktreePath, string name, CancellationToken ct = default);
    Task<GitResult> SwitchBranchAsync(string worktreePath, string name, CancellationToken ct = default);
}
