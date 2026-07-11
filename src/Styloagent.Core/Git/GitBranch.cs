namespace Styloagent.Core.Git;

/// <summary>A local git branch and whether it is the currently checked-out branch.</summary>
public sealed record GitBranch(string Name, bool IsCurrent);
