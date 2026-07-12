using System.Text;
using System.Text.Json;

namespace Styloagent.Core.Hooks;

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
    public static string BuildSettingsJson(string agentId, string hooksDir, string? hydrationFile = null)
    {
        string safeId = SanitizeAgentId(agentId);
        // Observe: write raw stdin JSON to a unique per-event file tagged with the agent id.
        // uuidgen ships with macOS and util-linux; the file is complete once the write exits.
        string observe = $"cat > \"{hooksDir}/{safeId}{Separator}$(uuidgen).json\"";

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
            hooks[ev] = (ev == "SessionStart" && reHydrate)
                ? Entry(SessionStartWithHydration(safeId, hooksDir, hydrationFile!))
                : Entry(observe);
        }

        var settings = new Dictionary<string, object> { ["hooks"] = hooks };
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

    /// <summary>The CLI args (<c>--settings &lt;json&gt;</c>) to append to a <c>claude</c> launch.</summary>
    public static IReadOnlyList<string> BuildSettingsArgs(string agentId, string hooksDir, string? hydrationFile = null)
        => new[] { "--settings", BuildSettingsJson(agentId, hooksDir, hydrationFile) };
}
