using System.Text.Json;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Hooks;

/// <summary>
/// Bridges Kilo into Styloagent's hook-drop channel. Kilo (a fork of OpenCode) has no Claude-style
/// hooks.json, but it auto-loads plugins from <c>.kilo/plugins/</c> at startup. This file installs a small
/// plugin (<see cref="PluginSource"/>) that maps Kilo's lifecycle/tool events to the same
/// <c>&lt;hooksDir&gt;/&lt;agentId&gt;__&lt;uuid&gt;.json</c> drop files <see cref="HookChannel"/> already
/// consumes — so a Kilo agent's state, activity and timeline work exactly like a Claude agent's, with no
/// CLI flags involved.
///
/// The plugin is a no-op outside a Styloagent spawn: it reads <c>STYLOAGENT_HOOKS_DIR</c> +
/// <c>STYLOAGENT_AGENT_ID</c> from the environment, and writes nothing when they are absent.
/// </summary>
public static class KiloHooksPlugin
{
    /// <summary>The plugin file name inside a project's <c>.kilo/plugins/</c> directory.</summary>
    public const string FileName = "styloagent-hooks.js";

    /// <summary>Returns the plugin path for a project/worktree root.</summary>
    public static string PathFor(string projectRoot)
        => Path.Combine(projectRoot, ".kilo", "plugins", FileName);

    /// <summary>
    /// Writes the observation plugin into <paramref name="projectRoot"/>'s <c>.kilo/plugins/</c>
    /// directory (idempotent). Best-effort: a failure never blocks the spawn — the agent simply runs
    /// without fleet observation, the same graceful degradation as an unwired hooks channel.
    /// </summary>
    public static bool EnsureInstalled(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return false;
        try
        {
            var path = PathFor(projectRoot);
            if (File.Exists(path)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PluginSource);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds the per-agent <c>KILO_CONFIG_CONTENT</c> JSON injected as an env var for one spawned Kilo
    /// agent: the styloagent MCP server (with THIS agent's identity header), the fleet permission block,
    /// a cheap <c>small_model</c> for titles/summaries, and any extra instruction files (e.g. the
    /// overview's system prompt). Per-agent config means no repo config file is ever mutated and each
    /// agent reaches the MCP server as itself.
    /// </summary>
    public static string BuildConfigContent(string prefix, Uri url, string token, FleetPermissionMode mode,
        IEnumerable<string>? instructions = null)
    {
        var instructionPaths = instructions?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var root = new Dictionary<string, object?>
        {
            ["mcp"] = new Dictionary<string, object?>
            {
                ["styloagent"] = new Dictionary<string, object?>
                {
                    ["type"] = "remote",
                    ["url"] = url.ToString(),
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["X-Styloagent-Agent"] = prefix,
                        ["Authorization"] = $"Bearer {token}",
                    },
                    ["enabled"] = true,
                },
            },
            ["permission"] = PermissionBlock(mode),
            ["small_model"] = AgentRuntimeProfile.KiloFlashModelId,
        };
        if (instructionPaths is { Count: > 0 })
            root["instructions"] = instructionPaths;
        return JsonSerializer.Serialize(root);
    }

    private static object PermissionBlock(FleetPermissionMode mode) => mode switch
    {
        FleetPermissionMode.Bypass => "allow",
        FleetPermissionMode.Scoped => new Dictionary<string, object?>
        {
            ["edit"] = new Dictionary<string, string> { ["*"] = "allow" },
            ["write"] = new Dictionary<string, string> { ["*"] = "allow" },
            ["bash"] = new Dictionary<string, string> { ["*"] = "allow" },
            ["read"] = new Dictionary<string, string> { ["*"] = "allow" },
            ["styloagent_*"] = "allow",
        },
        _ => new Dictionary<string, string> { ["*"] = "ask" },
    };

    /// <summary>
    /// The plugin source. Deliberately dependency-free (Node builtins only) and exception-safe: every
    /// handler is wrapped so a malformed event can never crash the Kilo process. Event payload shapes are
    /// read defensively (candidate keys) because the Kilo/OpenCode event schema is not stable.
    /// </summary>
    public const string PluginSource =
"""
// Styloagent fleet-observation plugin — bridges Kilo events into Styloagent's hook-drop channel.
// Installed into .kilo/plugins/ by the cockpit; a no-op when the Styloagent env is absent.
import { writeFileSync } from "node:fs";
import { randomUUID } from "node:crypto";

const hooksDir = process.env.STYLOAGENT_HOOKS_DIR;
const agentId = process.env.STYLOAGENT_AGENT_ID || "agent";

function drop(payload) {
  if (!hooksDir) return;
  try {
    const file = `${hooksDir}/${agentId}__${randomUUID()}.json`;
    writeFileSync(file, JSON.stringify(payload));
  } catch {}
}

// Kilo tool args are camelCase (filePath, oldString, newString, command); Styloagent's parser expects
// the Claude snake_case names. Map keys and keep non-string values JSON-encoded.
function snakeArgs(input) {
  const out = {};
  for (const [k, v] of Object.entries(input || {})) {
    const key = k.replace(/[A-Z]/g, (m) => "_" + m.toLowerCase());
    out[key] = v !== null && typeof v === "object" ? JSON.stringify(v) : v;
  }
  return out;
}

function eventBase(event) {
  const p = event?.properties || {};
  return {
    session_id: p.sessionID || p.sessionId || p.session_id || undefined,
    cwd: p.directory || p.cwd || undefined,
  };
}

export const StyloagentHooks = async (ctx) => {
  return {
    event: async ({ event }) => {
      const type = event?.type;
      const base = eventBase(event);
      switch (type) {
        case "session.created":
          drop({ hook_event_name: "SessionStart", ...base });
          break;
        case "session.idle":
          drop({ hook_event_name: "Notification", notification_type: "idle_prompt", ...base });
          break;
        case "session.error":
          drop({ hook_event_name: "Notification", notification_type: "agent_needs_input",
                 message: String(event?.properties?.error || "kilo session error"), ...base });
          break;
        case "permission.asked":
          drop({ hook_event_name: "PermissionRequest",
                 message: String(event?.properties?.prompt || event?.properties?.message || "permission requested"),
                 tool_name: event?.properties?.tool, ...base });
          break;
        case "session.deleted":
          drop({ hook_event_name: "SessionEnd", ...base });
          break;
      }
    },
    "tool.execute.before": async (input) => {
      drop({ hook_event_name: "PreToolUse", tool_name: input?.tool,
             tool_input: snakeArgs(input?.args || input?.input), session_id: input?.sessionID, cwd: input?.cwd });
    },
    "tool.execute.after": async (input) => {
      drop({ hook_event_name: "PostToolUse", tool_name: input?.tool,
             tool_input: snakeArgs(input?.args || input?.input), session_id: input?.sessionID, cwd: input?.cwd });
    },
  };
};
""";
}
