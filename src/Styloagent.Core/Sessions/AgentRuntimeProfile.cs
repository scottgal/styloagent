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
        AgentRuntimeKind.Kilo => Kilo,
        AgentRuntimeKind.ClaudeDeepSeek => ClaudeDeepSeek,
        _ => Claude,
    };

    /// <summary>
    /// Preferred DeepSeek model for the overview / repo-root agents (the fleet's planner). Uses the
    /// DIRECT DeepSeek provider id (not the <c>kilo/</c> gateway prefix) so the user's DeepSeek API key
    /// authenticates it — the kilo gateway requires a separate sign-in.
    /// </summary>
    public static readonly string KiloDefaultModelId = "deepseek/deepseek-v4-pro";

    /// <summary>Preferred DeepSeek model for spawned specialist agents (fast, cheap, focused). Same direct-provider id.</summary>
    public static readonly string KiloFlashModelId = "deepseek/deepseek-v4-flash";

    /// <summary>Reasoning-effort variants Kilo accepts for DeepSeek models (the <c>--variant</c> flag).</summary>
    public static readonly string[] KiloEfforts = { "default", "low", "medium", "high", "max" };

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

    /// <summary>
    /// The Kilo CLI, run as its interactive TUI (the operator sees kilo's real UI in the pane, exactly
    /// like Claude's). The prompt is injected by typing + Enter; model comes from <c>--model provider/model</c>;
    /// effort is at the agent's discretion (the TUI has no <c>--variant</c>). MCP config, permissions and the
    /// fleet-observation hooks plugin are injected per-agent via <c>KILO_CONFIG_CONTENT</c> + env (config
    /// <c>permission</c> drives auto-approval in the TUI — no <c>--auto</c> needed), so no repo config file is
    /// mutated. Hooks are fully wired (not skipped): the plugin writes drop files the
    /// <see cref="Styloagent.Core.Hooks.HookChannel"/> consumes, driving the live state machine.
    /// </summary>
    public static readonly AgentRuntimeProfile Kilo = new(
        AgentRuntimeKind.Kilo,
        Command: "kilo",
        DisplayName: "Kilo",
        SupportsClaudeSettingsHooks: false,
        SupportsInitialPromptArgument: false,
        UsesConfigLayerHooks: false,
        DefaultModel: KiloDefaultModelId,
        PtyWakeString: "\r",
        SkipHookStateMachine: false,
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
    /// flags); Codex uses its own sandbox/approval flags; Kilo runs headless <c>kilo run</c>, where any
    /// not-auto-approved permission request is auto-rejected (the run exits 1), so fleet agents always launch
    /// with <c>--auto</c>. The permission-mode distinction is still reflected in the per-agent
    /// <c>KILO_CONFIG_CONTENT</c> permission block (see the launch pipeline).
    /// </summary>
    public IReadOnlyList<string> PermissionArgs(FleetPermissionMode mode) => Kind switch
    {
        AgentRuntimeKind.Codex => mode switch
        {
            FleetPermissionMode.Bypass => new[] { "--dangerously-bypass-approvals-and-sandbox" },
            FleetPermissionMode.Scoped => new[] { "--sandbox", "workspace-write", "--ask-for-approval", "on-request" },
            _ => Array.Empty<string>(),
        },
        // Kilo TUI: approvals come from the per-agent KILO_CONFIG_CONTENT permission block, so no CLI flag.
        AgentRuntimeKind.Kilo => Array.Empty<string>(),
        _ => HookSettings.PermissionArgs(mode),
    };

    /// <summary>
    /// Builds the --model and --effort (or equivalent) CLI arguments for this runtime.
    /// Kilo uses <c>--model provider/model</c> + <c>--variant</c>. Codex uses --config model_reasoning_effort=.
    /// Claude family uses --model and --effort.
    /// </summary>
    public IReadOnlyList<string> ModelEffortArgs(string? model, string? effort)
    {
        var args = new List<string>();
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : DefaultModel;
        var effectiveEffort = !string.IsNullOrWhiteSpace(effort) &&
                              !effort.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? effort
            : null;

        if (Kind == AgentRuntimeKind.Kilo)
        {
            // Interactive TUI: --model only. The TUI has no --variant flag — reasoning effort is at the
            // agent's discretion (per cockpit policy).
            if (!string.IsNullOrWhiteSpace(effectiveModel))
            {
                args.Add("--model");
                args.Add(effectiveModel!);
            }
            return args;
        }

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
    /// Kilo has no CLI hook flags — its fleet-observation plugin (dropped into the project's
    /// <c>.kilo/plugins/</c>) is auto-loaded and writes the same drop files, so no args are needed here.
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
    /// Kilo and Codex take the prompt as a positional argument to <c>kilo run</c> / <c>codex</c>;
    /// Claude injects via PTY.
    /// </summary>
    public string? PromptArg(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !SupportsInitialPromptArgument)
            return null;

        return prompt;   // Kilo (`kilo run <prompt>`) and Codex (bare positional)
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
