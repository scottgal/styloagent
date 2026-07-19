namespace Styloagent.Core.Hooks;

/// <summary>
/// Builds Codex CLI hook configuration for one spawned agent. Codex reads hooks from config layers, and
/// accepts inline config via repeated <c>--config key=value</c> arguments, so Styloagent can attach
/// per-pane hook drops without writing user or project Codex files.
/// </summary>
public static class CodexHookSettings
{
    /// <summary>The Codex lifecycle events Styloagent observes through the shared hook drop directory.</summary>
    private static readonly string[] ObservedEvents =
    {
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "PreCompact",
        "PostCompact",
        "SubagentStart",
        "SubagentStop",
        "Stop",
    };

    /// <summary>
    /// Builds repeated <c>--config hooks.Event=[...]</c> arguments for Codex. Each hook writes raw stdin JSON
    /// into <paramref name="hooksDir"/> using the same filename convention consumed by <see cref="HookChannel"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildConfigArgs(string agentId, string hooksDir, string? hydrationFile = null,
        string? gateInvocation = null, string? repoRoot = null, string? caller = null)
    {
        string safeId = HookSettings.SanitizeAgentId(agentId);
        string drop = $"{hooksDir}/{safeId}__$(uuidgen).json";
        string observe = $"cat > \"{drop}\"";
        bool reHydrate = !string.IsNullOrWhiteSpace(hydrationFile);
        bool gate = !string.IsNullOrWhiteSpace(gateInvocation) && !string.IsNullOrWhiteSpace(repoRoot);

        var args = new List<string>(ObservedEvents.Length * 2 + 1)
        {
            "--dangerously-bypass-hook-trust",
        };

        foreach (string ev in ObservedEvents)
        {
            args.Add("--config");
            var command = ev switch
            {
                "SessionStart" when reHydrate => SessionStartWithHydration(safeId, hooksDir, hydrationFile!),
                "PreToolUse" when gate => PreToolUseGateCommand(safeId, hooksDir, gateInvocation!, caller ?? agentId, repoRoot!),
                "UserPromptSubmit" => Styloagent.Core.Channel.DeliveryHookCommands.ForUserPromptSubmit(observe, hooksDir, safeId),
                "Stop" => Styloagent.Core.Channel.DeliveryHookCommands.ForStop(drop, hooksDir, safeId),
                _ => observe,
            };
            args.Add($"hooks.{ev}=[{{matcher=\"*\",hooks=[{{type=\"command\",command={TomlString(command)},timeout=30,statusMessage=\"Styloagent hook\"}}]}}]");
        }

        return args;
    }

    private static string SessionStartWithHydration(string safeId, string hooksDir, string hydrationFile)
    {
        string drop = $"{hooksDir}/{safeId}__$(uuidgen).json";
        return
            $"d=$(cat); printf '%s' \"$d\" > \"{drop}\"; " +
            "case \"$d\" in *'\"compact\"'*|*'\"resume\"'*) " +
            $"[ -f \"{hydrationFile}\" ] && " +
            "printf '{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":%s}}' " +
            $"\"$(cat \"{hydrationFile}\")\" ;; esac";
    }

    private static string PreToolUseGateCommand(
        string safeId, string hooksDir, string gateInvocation, string caller, string repoRoot)
    {
        string drop = $"{hooksDir}/{safeId}__$(uuidgen).json";
        return $"d=$(cat); printf '%s' \"$d\" > \"{drop}\"; " +
               $"printf '%s' \"$d\" | {gateInvocation} {OwnershipGateCli.GateModeFlag} " +
               $"--caller {ShQuote(caller)} --root {ShQuote(repoRoot)}";
    }

    private static string ShQuote(string? s) => "'" + (s ?? string.Empty).Replace("'", "'\\''") + "'";

    /// <summary>Escapes a value as a TOML basic string for Codex <c>--config</c> values.</summary>
    private static string TomlString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }
}
