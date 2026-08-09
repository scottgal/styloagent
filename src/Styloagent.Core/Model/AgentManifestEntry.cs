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

    public static AgentRuntimeKind Parse(string? name)
    {
        // Normalize so every spelling maps: "ClaudeDeepSeek", "claude-deepseek", "CLAUDE_DEEPSEEK" etc.
        // A camelCase CommandParameter was silently parsing to Claude — the "Claude+DS launches Opus" bug.
        string? key = name?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return key switch
        {
            "codex" => AgentRuntimeKind.Codex,
            "kilo" => AgentRuntimeKind.Kilo,
            "claudedeepseek" => AgentRuntimeKind.ClaudeDeepSeek,
            _ => AgentRuntimeKind.Claude,
        };
    }
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
    ModelTier? Tier = null,
    string? Model = null,
    string? Effort = null,
    bool AutoStartPrompt = true);
