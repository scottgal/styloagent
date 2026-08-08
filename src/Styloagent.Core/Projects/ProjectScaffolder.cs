namespace Styloagent.Core.Projects;

/// <summary>
/// Ensures a project's <c>.styloagent</c> tree exists, writing default templates only when absent.
/// Idempotent; never overwrites files the project already has.
/// </summary>
public static class ProjectScaffolder
{
    public static ProjectConfig Ensure(string root)
    {
        var cfg = ProjectConfig.For(root);

        Directory.CreateDirectory(cfg.ConfigDir);
        Directory.CreateDirectory(cfg.LaunchPromptsDir);
        Directory.CreateDirectory(Path.Combine(cfg.EnvironmentsRoot, "definitions"));
        Directory.CreateDirectory(Path.Combine(cfg.EnvironmentsRoot, "ownership"));
        Directory.CreateDirectory(Path.Combine(cfg.BrowserRoot, "jobs"));
        Directory.CreateDirectory(Path.Combine(cfg.BrowserRoot, "artifacts"));
        foreach (string sub in new[] { "inbox", "outbox", Path.Combine("archive", "inbox"), Path.Combine("archive", "outbox") })
            Directory.CreateDirectory(Path.Combine(cfg.ChannelRoot, sub));

        if (!File.Exists(cfg.SystemPromptPath))
            File.WriteAllText(cfg.SystemPromptPath, DefaultTemplates.SystemPrompt);
        if (!File.Exists(cfg.ProtocolPath))
            File.WriteAllText(cfg.ProtocolPath, DefaultTemplates.Protocol);
        if (!File.Exists(cfg.FleetPolicyPath))
            File.WriteAllText(cfg.FleetPolicyPath, "maxFleet: 12\nmaxDepth: 3\n");
        if (!File.Exists(cfg.ModelPolicyPath))
            File.WriteAllText(cfg.ModelPolicyPath, DefaultTemplates.ModelPolicy);
        var environmentPolicy = Path.Combine(cfg.EnvironmentsRoot, "policy.yaml");
        if (!File.Exists(environmentPolicy))
            File.WriteAllText(environmentPolicy, "controlOwner: overview-\n");

        return cfg;
    }
}
