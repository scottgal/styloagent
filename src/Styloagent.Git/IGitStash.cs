using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>
/// Stash operations: save the working-tree state, pop it back, and list what is stashed.
/// </summary>
public interface IGitStash
{
    /// <summary>Stash the current working-tree changes, optionally tagged with <paramref name="message"/>.</summary>
    Task<GitResult> StashAsync(string worktreePath, string? message, CancellationToken ct = default);

    /// <summary>Apply and drop the most recent stash entry.</summary>
    Task<GitResult> StashPopAsync(string worktreePath, CancellationToken ct = default);

    /// <summary>Return a line-per-entry listing of all stash entries.</summary>
    Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string worktreePath, CancellationToken ct = default);
}
