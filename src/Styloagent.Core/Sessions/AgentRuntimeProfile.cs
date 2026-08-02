using Styloagent.Core.Hooks;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Runtime-specific launch contract for an agent CLI. This is the extension point for first-class
/// agent support: command name, permission flags, and whether Styloagent can inject its hook/MCP settings.
/// </summary>
public sealed record AgentRuntimeProfile(
    AgentRuntimeKind Kind,
    string Command,
    bool SupportsClaudeSettingsHooks,
    bool SupportsInitialPromptArgument,
    string? DefaultModel = null)
{
    public static AgentRuntimeProfile For(AgentRuntimeKind kind) => kind switch
    {
        AgentRuntimeKind.Codex => Codex,
        AgentRuntimeKind.DeepCode => DeepCode,
        AgentRuntimeKind.ClaudeDeepSeek => ClaudeDeepSeek,
        _ => Claude,
    };

    public static readonly AgentRuntimeProfile Claude = new(
        AgentRuntimeKind.Claude,
        Command: "claude",
        SupportsClaudeSettingsHooks: true,
        SupportsInitialPromptArgument: false);

    public static readonly AgentRuntimeProfile Codex = new(
        AgentRuntimeKind.Codex,
        Command: "codex",
        SupportsClaudeSettingsHooks: false,
        // `codex [PROMPT]` is accepted before its interactive TUI starts. Supplying startup/revival
        // work this way avoids losing a PTY-injected prompt while Codex is still drawing its banner.
        SupportsInitialPromptArgument: true,
        // The Codex CLI default model varies by version; pin to its strongest coding model so
        // every spawn (UI button, MCP, rehydrate) gets the right model without the caller guessing.
        DefaultModel: "gpt-5-codex");

    public static readonly AgentRuntimeProfile DeepCode = new(
        AgentRuntimeKind.DeepCode,
        Command: "deepcode",
        SupportsClaudeSettingsHooks: false,
        // DeepCode uses `-p <prompt>` on the CLI; handled specially in MainWindowViewModel
        // because it needs the -p flag, not a bare positional argument.
        SupportsInitialPromptArgument: true);

    public static readonly AgentRuntimeProfile ClaudeDeepSeek = new(
        AgentRuntimeKind.ClaudeDeepSeek,
        Command: "claude",
        SupportsClaudeSettingsHooks: true,
        SupportsInitialPromptArgument: false,
        // Match the env-var default so `--model` is consistent even when deepseek.env is missing.
        DefaultModel: "deepseek-v4-pro[1m]");

    /// <summary>
    /// Runtime-native permission flags. Claude's scoped mode is mostly expressed in its settings JSON;
    /// Codex uses its own sandbox/approval flags. DeepCode has no CLI permission flags — permissions
    /// are configured in its settings.json.
    /// </summary>
    public IReadOnlyList<string> PermissionArgs(FleetPermissionMode mode) => Kind switch
    {
        AgentRuntimeKind.Codex => mode switch
        {
            FleetPermissionMode.Bypass => new[] { "--dangerously-bypass-approvals-and-sandbox" },
            FleetPermissionMode.Scoped => new[] { "--sandbox", "workspace-write", "--ask-for-approval", "on-request" },
            _ => Array.Empty<string>(),
        },
        AgentRuntimeKind.DeepCode => Array.Empty<string>(),   // no CLI permission flags — uses settings.json
        _ => HookSettings.PermissionArgs(mode),
    };
}
