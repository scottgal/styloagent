namespace Styloagent.Core.Git;

/// <summary>How a single path changed in the working tree.</summary>
public enum GitChangeKind { Added, Modified, Deleted, Renamed, Untracked, Conflicted }

/// <summary>One changed path in a worktree.</summary>
public sealed record GitChange(string Path, GitChangeKind Kind, bool Staged, bool Unstaged);

/// <summary>A worktree's status: dirtiness, ahead/behind vs upstream, conflicts, and the changes.</summary>
public sealed record GitStatus(
    bool IsDirty, int Ahead, int Behind, bool HasConflicts, IReadOnlyList<GitChange> Changes)
{
    public static GitStatus Clean { get; } = new(false, 0, 0, false, System.Array.Empty<GitChange>());
}
