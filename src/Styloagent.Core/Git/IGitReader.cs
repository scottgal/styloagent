namespace Styloagent.Core.Git;

/// <summary>Reads git worktree information for a repository (read-only).</summary>
public interface IGitReader
{
    /// <summary>
    /// Lists the worktrees of the repo at <paramref name="repoRoot"/>. Returns an empty
    /// list if the path is not a git repo / git is unavailable — never throws.
    /// </summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repoRoot, CancellationToken ct = default);
}
