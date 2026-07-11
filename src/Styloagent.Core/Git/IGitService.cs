namespace Styloagent.Core.Git;

/// <summary>
/// The git-operations seam the app/VMs call. Process-backed impl lives in <c>Styloagent.Git</c>;
/// faked in tests. Every method is tolerant — failures surface as a failed <see cref="GitResult"/>,
/// never an exception.
/// </summary>
public interface IGitService
{
    Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default);
    Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default);
    Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default);
    Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default);
    Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default);
}
