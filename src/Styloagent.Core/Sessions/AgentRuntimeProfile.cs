using Styloagent.Core.Hooks;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Single source of truth for everything runtime-specific: CLI command, hook wiring, permission
/// flags, model/effort arg assembly, PTY behaviour, transcript reader, and display identity.
/// Adding a new runtime means adding one static instance here and one entry in agent-capabilities.json.
/// </summary>
public sealed record AgentRuntimeProfile(
    AgentRuntimeKind Kind,
    string Command,
    string DisplayName,
    bool SupportsClaudeSettingsHooks,
    bool SupportsInitialPromptArgument,
    bool UsesConfigLayerHooks,
    string? DefaultModel = null,
    string PtyWakeString = "1\r",
    bool SkipHookStateMachine = false,
    bool UseCodexTranscriptReader = false,
    string DefaultLaunchPromptTemplate = "")
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
        DisplayName: "Claude",
        SupportsClaudeSettingsHooks: true,
        SupportsInitialPromptArgument: false,
        UsesConfigLayerHooks: false,
        PtyWakeString: "1\r",
        DefaultLaunchPromptTemplate: "You are agent '{0}'. Begin your work.");

    public static readonly AgentRuntimeProfile Codex = new(
        AgentRuntimeKind.Codex,
        Command: "codex",
        DisplayName: "Codex",
        SupportsClaudeSettingsHooks: false,
        SupportsInitialPromptArgument: true,
        UsesConfigLayerHooks: true,
        DefaultModel: "gpt-5-codex",
        PtyWakeString: "\r",
        SkipHookStateMachine: true,
        UseCodexTranscriptReader: true,
        DefaultLaunchPromptTemplate:
            "You are the '{0}' Styloagent workspace agent. Read .styloagent/PROTOCOL.md and your mission doc if present, check the fleet inbox, then carry out your assigned task.");

    public static readonly AgentRuntimeProfile DeepCode = new(
        AgentRuntimeKind.DeepCode,
        Command: "deepcode",
        DisplayName: "DeepCode",
        SupportsClaudeSettingsHooks: false,
        SupportsInitialPromptArgument: true,
        UsesConfigLayerHooks: true,
        PtyWakeString: "\r",
        SkipHookStateMachine: true,
        UseCodexTranscriptReader: true,
        DefaultLaunchPromptTemplate:
            "You are the '{0}' Styloagent workspace agent. Read .styloagent/PROTOCOL.md and your mission doc if present, check the fleet inbox, then carry out your assigned task.");

    public static readonly AgentRuntimeProfile ClaudeDeepSeek = new(
        AgentRuntimeKind.ClaudeDeepSeek,
        Command: "claude",
        DisplayName: "Claude+DeepSeek",
        SupportsClaudeSettingsHooks: true,
        SupportsInitialPromptArgument: false,
        UsesConfigLayerHooks: false,
        DefaultModel: "deepseek-v4-pro[1m]",
        PtyWakeString: "1\r",
        DefaultLaunchPromptTemplate: "You are agent '{0}'. Begin your work.");

    /// <summary>
    /// Runtime-native permission flags. Claude family uses HookSettings.PermissionArgs (scoped/permission-mode
    /// flags); Codex uses its own sandbox/approval flags; DeepCode has no CLI permission flags.
    /// </summary>
    public IReadOnlyList<string> PermissionArgs(FleetPermissionMode mode) => Kind switch
    {
        AgentRuntimeKind.Codex => mode switch
        {
            FleetPermissionMode.Bypass => new[] { "--dangerously-bypass-approvals-and-sandbox" },
            FleetPermissionMode.Scoped => new[] { "--sandbox", "workspace-write", "--ask-for-approval", "on-request" },
            _ => Array.Empty<string>(),
        },
        AgentRuntimeKind.DeepCode => Array.Empty<string>(),
        _ => HookSettings.PermissionArgs(mode),
    };

    /// <summary>
    /// Builds the --model and --effort (or equivalent) CLI arguments for this runtime.
    /// DeepCode reads these from settings.json (no CLI flags). Codex uses --config model_reasoning_effort=.
    /// Claude family uses --model and --effort.
    /// </summary>
    public IReadOnlyList<string> ModelEffortArgs(string? model, string? effort)
    {
        if (Kind == AgentRuntimeKind.DeepCode)
            return Array.Empty<string>();

        var args = new List<string>();
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : DefaultModel;
        if (!string.IsNullOrWhiteSpace(effectiveModel))
        {
            args.Add("--model");
            args.Add(effectiveModel!);
        }
        var effectiveEffort = !string.IsNullOrWhiteSpace(effort) &&
                              !effort.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? effort
            : null;
        if (!string.IsNullOrWhiteSpace(effectiveEffort))
        {
            if (Kind == AgentRuntimeKind.Codex)
            {
                args.Add("--config");
                args.Add($"model_reasoning_effort={TomlString(effectiveEffort!)}");
            }
            else
            {
                args.Add("--effort");
                args.Add(effectiveEffort!);
            }
        }
        return args;
    }

    /// <summary>
    /// Builds the hook configuration CLI arguments for one spawned agent. Delegates to the right
    /// hook builder: ConfigHookSettings (Codex, DeepCode) or HookSettings (Claude family).
    /// </summary>
    public IReadOnlyList<string> BuildHookArgs(
        string hookId, string hooksDir, string? hydrationFile = null,
        string? gateInvocation = null, string? repoRoot = null, string? caller = null)
    {
        if (UsesConfigLayerHooks)
            return ConfigHookSettings.BuildConfigArgs(hookId, hooksDir, hydrationFile, gateInvocation, repoRoot, caller);

        // Claude family: hook settings are injected via --settings JSON, not CLI args.
        // The --settings flag is handled separately in the launch pipeline.
        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns the CLI prompt argument for this runtime, or null if the prompt is injected via PTY.
    /// DeepCode uses -p, Codex uses a bare positional argument, Claude injects via PTY.
    /// </summary>
    public string? PromptArg(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !SupportsInitialPromptArgument)
            return null;

        return Kind == AgentRuntimeKind.DeepCode
            ? $"-p {prompt}"   // DeepCode: -p <prompt>
            : prompt;           // Codex: bare positional
    }

    /// <summary>
    /// Formats the default launch prompt for a new agent using this runtime's template.
    /// </summary>
    public string DefaultLaunchPrompt(string prefix) =>
        string.Format(DefaultLaunchPromptTemplate, prefix);

    /// <summary>Escapes a value as a TOML basic string for --config values.</summary>
    public static string TomlString(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
