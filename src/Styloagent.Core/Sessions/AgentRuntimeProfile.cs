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
    bool SupportsInitialPromptArgument)
{
    public static AgentRuntimeProfile For(AgentRuntimeKind kind) => kind switch
    {
        AgentRuntimeKind.Codex => Codex,
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
        SupportsInitialPromptArgument: true);

    /// <summary>
    /// Runtime-native permission flags. Claude's scoped mode is mostly expressed in its settings JSON;
    /// Codex uses its own sandbox/approval flags and deliberately does not receive Claude hook settings.
    /// </summary>
    public IReadOnlyList<string> PermissionArgs(FleetPermissionMode mode) => Kind switch
    {
        AgentRuntimeKind.Codex => mode switch
        {
            FleetPermissionMode.Bypass => new[] { "--dangerously-bypass-approvals-and-sandbox" },
            FleetPermissionMode.Scoped => new[] { "--sandbox", "workspace-write", "--ask-for-approval", "on-request" },
            _ => Array.Empty<string>(),
        },
        _ => HookSettings.PermissionArgs(mode),
    };
}
