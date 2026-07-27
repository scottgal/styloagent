namespace Styloagent.Core.Git;

/// <summary>A local git branch, whether it is checked out, and whether it is already reachable from HEAD.</summary>
public sealed record GitBranch(string Name, bool IsCurrent, bool IsMerged = false)
{
    public string MergeState => IsCurrent ? "current" : IsMerged ? "merged" : "unmerged";
}
