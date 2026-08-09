namespace Styloagent.Core.Model;

public enum AgentRuntimeKind { Claude, Codex, Kilo, ClaudeDeepSeek }

/// <summary>
/// Centralized runtime name mapping — the only place that converts between <see cref="AgentRuntimeKind"/>
/// and its string representation. Every other file calls these two methods instead of hand-writing switches.
/// </summary>
public static class AgentRuntime
{
    public static string Name(AgentRuntimeKind kind) => kind switch
    {
        AgentRuntimeKind.Codex => "codex",
        AgentRuntimeKind.Kilo => "kilo",
        AgentRuntimeKind.ClaudeDeepSeek => "claude-deepseek",
        _ => "claude",
    };

    public static AgentRuntimeKind Parse(string? name) =>
        string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase) ? AgentRuntimeKind.Codex :
        string.Equals(name, "kilo", StringComparison.OrdinalIgnoreCase) ? AgentRuntimeKind.Kilo :
        string.Equals(name, "claude-deepseek", StringComparison.OrdinalIgnoreCase) ? AgentRuntimeKind.ClaudeDeepSeek :
        AgentRuntimeKind.Claude;
}

public sealed record AgentManifestEntry(
    string Prefix,
    string Repo,
    string Worktree,
    string LaunchPromptPath,
    string RestartPromptPath,
    string SavedContextPath,
    AgentTransport Transport,
    AgentRuntimeKind Runtime = AgentRuntimeKind.Claude,
    string? Model = null,
    string? Effort = null,
    bool AutoStartPrompt = true);
