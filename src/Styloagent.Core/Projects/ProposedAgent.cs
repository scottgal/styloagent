namespace Styloagent.Core.Projects;

/// <summary>One subsystem the overview agent proposes for the human to spawn.</summary>
public sealed record ProposedAgent(string Prefix, string Responsibility, string Dir, string LaunchPrompt,
    bool Worktree = false, string? JobType = null);
