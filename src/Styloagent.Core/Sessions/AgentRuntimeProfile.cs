using Styloagent.Core.Hooks;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Single source of truth for everything runtime-specific: CLI command, hook wiring, permission
/// flags, model/effort arg assembly, PTY behaviour, transcript reader, and display identity.
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
        AgentRuntimeKind.ClaudeDeepSeek => ClaudeDeepSeek,
        AgentRuntimeKind.Claude => Claude,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This runtime is not supported."),
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
        DefaultModel: "deepseek-flash",
        PtyWakeString: "1\r",
        DefaultLaunchPromptTemplate: "You are agent '{0}'. Begin your work.");

    /// <summary>
    /// Runtime-native permission flags. Claude family uses HookSettings.PermissionArgs (scoped/permission-mode
    /// flags); Codex uses its own sandbox/approval flags.
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

    /// <summary>
    /// Builds the --model and --effort (or equivalent) CLI arguments for this runtime.
    /// Codex uses --config model_reasoning_effort=. Claude family uses --model and --effort.
    /// </summary>
    public IReadOnlyList<string> ModelEffortArgs(string? model, string? effort, Styloagent.Core.Model.ModelTier? tier = null)
    {
        var args = new List<string>();
        // An explicit model is the only model id this layer may launch. In particular, Codex's old tier
        // aliases (gpt-5 / gpt-5-codex) are not valid account models. The spawner resolves a tier against
        // the live Codex capability catalog; when that selection is unavailable we omit --model and let
        // Codex use the account's configured default.
        var effectiveModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : Kind != AgentRuntimeKind.Codex && tier is not null
                ? Styloagent.Core.Model.ModelTierResolver.ResolveModel(Kind, tier.Value)
                : DefaultModel;
        var effectiveEffort = !string.IsNullOrWhiteSpace(effort) &&
                              !effort.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? effort
            : null;

        if (!string.IsNullOrWhiteSpace(effectiveModel))
        {
            args.Add("--model");
            args.Add(effectiveModel!);
        }
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
    /// Builds the hook configuration CLI arguments for one spawned agent. Claude family: hook settings are
    /// injected via --settings JSON (handled separately in the launch pipeline). Codex uses --config hooks.*.
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
    /// Codex takes the prompt as a positional argument; Claude injects via PTY.
    /// </summary>
    public string? PromptArg(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !SupportsInitialPromptArgument)
            return null;

        return prompt;   // Codex (bare positional)
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
