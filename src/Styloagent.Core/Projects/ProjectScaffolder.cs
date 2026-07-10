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
        foreach (string sub in new[] { "inbox", "outbox", Path.Combine("archive", "inbox"), Path.Combine("archive", "outbox") })
            Directory.CreateDirectory(Path.Combine(cfg.ChannelRoot, sub));

        if (!File.Exists(cfg.SystemPromptPath))
            File.WriteAllText(cfg.SystemPromptPath, DefaultTemplates.SystemPrompt);
        if (!File.Exists(cfg.ProtocolPath))
            File.WriteAllText(cfg.ProtocolPath, DefaultTemplates.Protocol);

        return cfg;
    }
}
