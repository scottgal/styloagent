namespace Styloagent.Core.Git;

/// <summary>
/// The git-operations seam the app/VMs call. Process-backed impl lives in <c>Styloagent.Git</c>;
/// faked in tests. Every method is tolerant — failures surface as a failed <see cref="GitResult"/>,
/// never an exception.
/// </summary>
public interface IGitService
{
    Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default);

    /// <summary>
    /// Resolves <paramref name="path"/> to the canonical root of the git repository that contains it
    /// (<c>git rev-parse --show-toplevel</c>), or <c>null</c> if the path is not inside a git repo /
    /// git is unavailable — never throws. Any location inside a repo (including a subdirectory)
    /// normalizes to the same root, so callers keying on repo identity — e.g. opening a NON-primary
    /// repo as a second federated instance — stay stable regardless of which folder was picked.
    /// The default returns <c>null</c> (unresolvable) so fakes need no change; the process-backed
    /// <c>Styloagent.Git.GitService</c> overrides it with the real read.
    /// </summary>
    Task<string?> ResolveRepoRootAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default);
    Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default);
    Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default);
    Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default);
    Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default);
}
