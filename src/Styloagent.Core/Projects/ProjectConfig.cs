namespace Styloagent.Core.Projects;

/// <summary>Resolved paths for a project's Styloagent state (all under &lt;root&gt;/.styloagent).</summary>
public sealed record ProjectConfig(
    string Root,
    string ConfigDir,
    string SystemPromptPath,
    string ProtocolPath,
    string ChannelRoot,
    string LaunchPromptsDir,
    string FleetPolicyPath,
    string PriorityPolicyPath,
    string BriefPath,
    string SpecPath,
    string ArchitecturePath,
    string IssuesDir,
    string GitPolicyPath,
    string RouterRoot,
    string EnvironmentsRoot,
    string BrowserRoot,
    string ModelPolicyPath,
    string MemoryRagPath)
{
    /// <summary>Builds the config paths for a project root. Pure — performs no I/O.</summary>
    public static ProjectConfig For(string root)
    {
        string cfg = Path.Combine(root, ".styloagent");
        return new ProjectConfig(
            Root: root,
            ConfigDir: cfg,
            SystemPromptPath: Path.Combine(cfg, "system-prompt.md"),
            ProtocolPath: Path.Combine(cfg, "PROTOCOL.md"),
            ChannelRoot: Path.Combine(cfg, "channel"),
            LaunchPromptsDir: Path.Combine(cfg, "launch-prompts"),
            FleetPolicyPath: Path.Combine(cfg, "fleet.yaml"),
            PriorityPolicyPath: Path.Combine(cfg, "priority-policy.yaml"),
            BriefPath: Path.Combine(cfg, "brief.md"),
            SpecPath: Path.Combine(cfg, "spec.md"),
            ArchitecturePath: Path.Combine(cfg, "architecture.md"),
            IssuesDir: Path.Combine(cfg, "issues"),
            GitPolicyPath: Path.Combine(cfg, "git-policy.yaml"),
            RouterRoot: Path.Combine(cfg, "router"),
            EnvironmentsRoot: Path.Combine(cfg, "environments"),
            BrowserRoot: Path.Combine(cfg, "browser"),
            ModelPolicyPath: Path.Combine(cfg, "model-policy.yaml"),
            MemoryRagPath: Path.Combine(cfg, "memory-rag.yaml"));
    }
}
