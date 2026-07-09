namespace Styloagent.Core.Git;

/// <summary>One git worktree: its checkout path, current branch (null if detached), and HEAD sha.</summary>
public sealed record GitWorktree(string Path, string? Branch, string Head)
{
    /// <summary>A short human name: the branch, else the last path segment.</summary>
    public string Name =>
        !string.IsNullOrEmpty(Branch)
            ? Branch!
            : System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
}
