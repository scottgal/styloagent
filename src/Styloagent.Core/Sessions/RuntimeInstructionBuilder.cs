using Styloagent.Core.Projects;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Builds the developer instructions supplied at process launch. Project documents are an overlay, not
/// the runtime authority: the bundled instructions are always last so a stale copied template cannot
/// supersede current fleet safety and protocol rules.
/// </summary>
public static class RuntimeInstructionBuilder
{
    public static string Build(string? systemPromptOverlay, string? protocolOverlay, string repoRoot, string overviewPrefix)
    {
        var parts = new List<string>();
        AppendOverlay(parts, "system prompt", systemPromptOverlay);
        AppendOverlay(parts, "protocol", protocolOverlay);
        parts.Add($"""
            ## Runtime repository identity

            Repository root: {repoRoot}
            Overview prefix: {overviewPrefix}
            This identity is supplied by the running Cockpit. Do not copy it into reusable project templates.
            """);
        parts.Add($"""
            ## Styloagent canonical runtime instructions (higher precedence)

            {DefaultTemplates.SystemPrompt.Trim()}

            {DefaultTemplates.Protocol.Trim()}
            """);
        return string.Join("\n\n", parts);
    }

    public static string BuildForProject(ProjectConfig config, string overviewPrefix)
        => Build(Read(config.SystemPromptPath), Read(config.ProtocolPath), config.Root, overviewPrefix);

    private static void AppendOverlay(List<string> parts, string name, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        parts.Add($"## Project-local {name} overlay (lower precedence)\n\n{text.Trim()}");
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
