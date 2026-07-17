using System.Text;
using System.Text.Json;

namespace Styloagent.Core.Hooks;

/// <summary>
/// How much permission a fleet agent is granted at launch, so it can act without a human approving every
/// tool use. <see cref="Prompt"/> = default Claude behaviour (approve everything). <see cref="Scoped"/> =
/// auto-accept file edits and pre-approve the styloagent MCP tools, but still prompt for other commands.
/// <see cref="Bypass"/> = skip all permission prompts (fully autonomous — trusted repos only).
/// </summary>
public enum FleetPermissionMode { Prompt, Scoped, Bypass }

/// <summary>
/// Builds the <c>claude --settings &lt;json&gt;</c> blob that streams hook events for one agent (§4.4),
/// and the filename convention used to correlate a dropped event file back to its agent.
///
/// Each hook runs (via <c>sh -c</c> on macOS/Linux) a command that writes the raw stdin JSON to a
/// uniquely-named file under the shared hooks directory:
/// <c>&lt;hooksDir&gt;/&lt;agentId&gt;__&lt;uuid&gt;.json</c>. A file-drop (rather than a socket) needs no
/// listener process, reuses the project's file-watch model, and each file is complete when the
/// <c>cat</c> that wrote it exits — so the watcher never reads a partial event.
/// </summary>
public static class HookSettings
{
    /// <summary>Separator between the agent id and the unique token in a drop-file name.</summary>
    private const string Separator = "__";

    /// <summary>
    /// The hook events we observe. Deliberately limited to the well-documented core events so an
    /// unknown key can never make <c>claude</c> fail to launch.
    /// </summary>
    private static readonly string[] ObservedEvents =
    {
        "SessionStart",
        "SessionEnd",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "Notification",
        "Stop",
    };

    /// <summary>
    /// Reduces an arbitrary agent identifier to a filename- and shell-safe token containing only
    /// letters, digits and dashes — guaranteeing it never contains the <see cref="Separator"/>.
    /// </summary>
    public static string SanitizeAgentId(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return "agent";
        var sb = new StringBuilder(agentId.Length);
        foreach (char c in agentId)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        string s = sb.ToString();
        return s.Length == 0 ? "agent" : s;
    }

    /// <summary>
    /// Recovers the (sanitized) agent id from a drop-file name of the form
    /// <c>&lt;agentId&gt;__&lt;uuid&gt;.json</c>. Returns null if the name doesn't match.
    /// </summary>
    public static string? AgentIdFromFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        int idx = name.IndexOf(Separator, StringComparison.Ordinal);
        return idx > 0 ? name[..idx] : null;
    }

    /// <summary>
    /// Builds the value for <c>claude --settings</c> that streams this agent's hook events into
    /// <paramref name="hooksDir"/>. When <paramref name="hydrationFile"/> is supplied, the
    /// <c>SessionStart</c> hook ALSO re-injects the agent's hydration instructions on
    /// <c>source=compact|resume</c> — the guard against an agent compacting away its own identity.
    /// The returned string is compact JSON, ready to pass as one CLI arg.
    /// </summary>
    /// <summary>
    /// The extra CLI flag(s) a permission mode needs beyond the <c>--settings</c> block. Only
    /// <see cref="FleetPermissionMode.Bypass"/> needs one (<c>--dangerously-skip-permissions</c>); Scoped is
    /// expressed entirely inside the settings JSON (see <see cref="BuildSettingsJson"/>).
    /// </summary>
    public static IReadOnlyList<string> PermissionArgs(FleetPermissionMode mode)
        => mode == FleetPermissionMode.Bypass
            ? new[] { "--dangerously-skip-permissions" }
            : Array.Empty<string>();

    public static string BuildSettingsJson(string agentId, string hooksDir, string? hydrationFile = null,
        FleetPermissionMode permissionMode = FleetPermissionMode.Prompt,
        string? gateInvocation = null, string? repoRoot = null, string? caller = null)
    {
        string safeId = SanitizeAgentId(agentId);
        // Observe: write raw stdin JSON to a unique per-event file tagged with the agent id.
        // uuidgen ships with macOS and util-linux; the file is complete once the write exits.
        string observe = $"cat > \"{hooksDir}/{safeId}{Separator}$(uuidgen).json\"";

        // When a gate invocation + repo root are supplied, the PreToolUse hook ALSO runs the ownership gate:
        // it still drops the event (so status badges don't regress) AND pipes it to the app in headless
        // gate-mode, whose stdout (a deny payload for a cross-owner write, or nothing) becomes the hook's.
        bool gate = !string.IsNullOrWhiteSpace(gateInvocation) && !string.IsNullOrWhiteSpace(repoRoot);

        // hooks: { "<Event>": [ { "hooks": [ { "type":"command", "command":"..." } ] } ], ... }
        static List<Dictionary<string, object>> Entry(string command) => new()
        {
            new()
            {
                ["hooks"] = new List<Dictionary<string, string>>
                {
                    new() { ["type"] = "command", ["command"] = command },
                },
            },
        };

        bool reHydrate = !string.IsNullOrWhiteSpace(hydrationFile);
        var hooks = new Dictionary<string, object>();
        foreach (string ev in ObservedEvents)
        {
            hooks[ev] = ev switch
            {
                "SessionStart" when reHydrate => Entry(SessionStartWithHydration(safeId, hooksDir, hydrationFile!)),
                "PreToolUse" when gate        => Entry(PreToolUseGateCommand(safeId, hooksDir, gateInvocation!, caller ?? agentId, repoRoot!)),
                _                             => Entry(observe),
            };
        }

        var settings = new Dictionary<string, object> { ["hooks"] = hooks };

        // Scoped: auto-accept file edits and pre-approve the styloagent MCP tools, so agents coordinate and
        // do their work without a prompt per action, while other shell commands still gate. Bypass carries no
        // settings block (its --dangerously-skip-permissions flag covers everything); Prompt adds nothing.
        if (permissionMode == FleetPermissionMode.Scoped)
        {
            settings["permissions"] = new Dictionary<string, object>
            {
                ["allow"] = new[] { "mcp__styloagent" },
                ["defaultMode"] = "acceptEdits",
            };
        }
        return JsonSerializer.Serialize(settings);
    }

    /// <summary>
    /// The <c>SessionStart</c> command that still drops the raw event for observation AND, when the
    /// source is <c>compact</c> or <c>resume</c>, prints the agent's hydration text back to Claude as
    /// <c>additionalContext</c> (read from <paramref name="hydrationFile"/>, which holds a JSON string).
    /// Pure POSIX <c>sh</c>: a substring match on the payload, no jq.
    /// </summary>
    private static string SessionStartWithHydration(string safeId, string hooksDir, string hydrationFile)
    {
        string drop = $"{hooksDir}/{safeId}{Separator}$(uuidgen).json";
        return
            $"d=$(cat); printf '%s' \"$d\" > \"{drop}\"; " +
            "case \"$d\" in *'\"compact\"'*|*'\"resume\"'*) " +
            $"[ -f \"{hydrationFile}\" ] && " +
            "printf '{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":%s}}' " +
            $"\"$(cat \"{hydrationFile}\")\" ;; esac";
    }

    /// <summary>
    /// The PreToolUse hook command when ownership gating is on: read stdin ONCE, drop it for observation
    /// (badges), then pipe the same bytes to the app in headless gate-mode — whose stdout (a PreToolUse deny
    /// payload for a cross-owner write, or nothing) becomes the hook's stdout, so a blocked write is denied
    /// synchronously. <paramref name="caller"/> is the agent's own prefix (matched against ownership.yaml).
    /// </summary>
    private static string PreToolUseGateCommand(
        string safeId, string hooksDir, string gateInvocation, string caller, string repoRoot)
    {
        string drop = $"{hooksDir}/{safeId}{Separator}$(uuidgen).json";
        // POSIX single-quote caller + repoRoot so a spawn-supplied prefix or an odd repo path (spaces,
        // quotes, ';', '$', backticks) can't break out of the shell command — no injection via the gate.
        // gateInvocation arrives already single-quoted per token (see DefaultGateInvocation).
        return $"d=$(cat); printf '%s' \"$d\" > \"{drop}\"; " +
               $"printf '%s' \"$d\" | {gateInvocation} {OwnershipGateCli.GateModeFlag} " +
               $"--caller {ShQuote(caller)} --root {ShQuote(repoRoot)}";
    }

    /// <summary>
    /// POSIX single-quotes a value for safe interpolation into an <c>sh -c</c> command: wraps in single
    /// quotes and escapes any embedded single quote as <c>'\''</c>. Single quotes suppress ALL shell
    /// interpretation, so the value can never inject or terminate the command.
    /// </summary>
    private static string ShQuote(string? s) => "'" + (s ?? string.Empty).Replace("'", "'\\''") + "'";

    /// <summary>
    /// Best-effort command that re-invokes THIS app in headless gate-mode. <see cref="Environment.ProcessPath"/>
    /// is the running host: for a framework-dependent launch (<c>dotnet Styloagent.App.dll</c>) it's the
    /// <c>dotnet</c> muxer, so the app dll must be appended; for a native apphost it runs the app directly.
    /// </summary>
    public static string DefaultGateInvocation()
    {
        string host = Environment.ProcessPath ?? "dotnet";
        bool isMuxer = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
        // Single-quote each token so an install path with spaces/quotes is both correct AND injection-safe.
        if (!isMuxer) return ShQuote(host);
        string appDll = Path.Combine(AppContext.BaseDirectory, "Styloagent.App.dll");
        return $"{ShQuote(host)} {ShQuote(appDll)}";
    }

    /// <summary>The CLI args (<c>--settings &lt;json&gt;</c>) to append to a <c>claude</c> launch.</summary>
    public static IReadOnlyList<string> BuildSettingsArgs(string agentId, string hooksDir, string? hydrationFile = null,
        FleetPermissionMode permissionMode = FleetPermissionMode.Prompt,
        string? gateInvocation = null, string? repoRoot = null, string? caller = null)
        => new[] { "--settings", BuildSettingsJson(agentId, hooksDir, hydrationFile, permissionMode, gateInvocation, repoRoot, caller) };
}
