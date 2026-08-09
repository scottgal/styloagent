# Kilo runtime & model discovery — design notes

> Status: implemented (deepcode removed, kilo added). Owned by `overview-` / cockpit-.

## Runtime surface

| Runtime | CLI | Launch | Model/effort flags | MCP | Hooks |
|---|---|---|---|---|---|
| `claude` | `claude` (TUI) | prompt injected via PTY | `--model`, `--effort` | `--mcp-config` | `--settings` hooks.json |
| `codex` | `codex` | prompt as positional | `--config model_reasoning_effort=` | `--config mcp_servers.*` | `--config hooks.*` |
| `kilo` | `kilo run <prompt>` (headless, autonomous) | prompt as positional | `--model provider/model`, `--variant` | per-agent `KILO_CONFIG_CONTENT` env | `.kilo/plugins/styloagent-hooks.js` drop writer |
| `claude-deepseek` | `claude` + `deepseek.env` routing | prompt injected via PTY | `--model deepseek-v4-pro[1m]` | `--mcp-config` | `--settings` hooks.json |

Kilo runs as `kilo run <prompt> --model <id> --variant <effort> --auto` over a PTY. It is a one-shot
headless run (like Codex): it exits when the task completes and the cockpit treats the pane as a
re-spawnable ghost. `--auto` is always passed — without it a headless kilo run auto-rejects any
permission request and exits 1. The permission *mode* (Prompt/Scoped/Bypass) is still reflected in the
per-agent `permission` block inside `KILO_CONFIG_CONTENT`.

## Per-agent config, not repo mutation

Kilo reads MCP servers, permissions, models and instruction files from its config files
(`~/.config/kilo/kilo.json[c]`, project `kilo.json[c]`/`.kilo/`, plus the high-precedence inline
`KILO_CONFIG_CONTENT` env var). Styloagent never edits those files. Instead each spawned kilo agent gets
a `KILO_CONFIG_CONTENT` JSON built by `KiloHooksPlugin.BuildConfigContent` containing:

- `mcp.styloagent` — remote HTTP MCP server, with **this agent's** `X-Styloagent-Agent` header + bearer
  token, so every agent reaches the cockpit as itself (the old deepcode `.deepcode/settings.json`
  identity problem is gone).
- `permission` — mapped from the cockpit's fleet permission mode (informational; `--auto` dominates).
- `small_model` — the flash model for titles/summaries.
- `instructions` — absolute paths to written instruction files for `--append-system-prompt` content
  (e.g. the overview's system prompt), written to `<worktree>/.kilo/instructions/<prefix>-N.md`.

Env also carries `STYLOAGENT_AGENT_ID` and `STYLOAGENT_HOOKS_DIR` for the observation plugin.

## Kilo hooks — the plugin bridge

Kilo (a fork of OpenCode) has no Claude-style `hooks.json`. Instead it auto-loads plugins from
`.kilo/plugins/` (and `~/.config/kilo/plugins/`). `KiloHooksPlugin.EnsureInstalled` writes
`styloagent-hooks.js` into the agent's working tree (and the repo root). The plugin maps Kilo's event
stream to the exact drop-file contract `HookChannel` already consumes
(`<hooksDir>/<agentId>__<uuid>.json`, `hook_event_name` etc.):

| Kilo event | Drop |
|---|---|
| `session.created` | `SessionStart` |
| `tool.execute.before` / `after` | `PreToolUse` / `PostToolUse` (camelCase args -> snake_case) |
| `session.idle` | `Notification(idle_prompt)` |
| `permission.asked` | `PermissionRequest` |
| `session.error` | `Notification(agent_needs_input)` |
| `session.deleted` | `SessionEnd` |

Because the drops are real, kilo agents get the full state machine (working / idle / needs-you),
activity detail and timeline — no `SkipHookStateMachine`. The plugin is a no-op without the env vars and
wrapped in try/catch so a malformed event can never crash kilo.

## Dynamic model discovery

`KiloModelDiscovery` runs the installed `kilo models` CLI (one `provider/model` per line) and parses
the catalog, e.g. `deepseek/deepseek-v4-pro`. Results are cached (5 min TTL) and refreshed in the
background — the UI thread never waits on the process. `BuildAgentCapabilities` overlays the live
catalog onto the static `agent-capabilities.json` / fallback list via `AgentCapabilities.WithKiloModels`,
so `agent_capabilities()` always shows every model actually available on this machine. Claude/Codex stay
curated (their model-listing commands are interactive TUIs, not scriptable).

## Default model policy

- Overview / repo-root agents default to `deepseek/deepseek-v4-pro` (`AgentRuntimeProfile.DefaultModel`).
- Spawned agents default to `deepseek/deepseek-v4-flash` (applied in `SpawnChildAsync`).
- Reasoning **effort is at the agent's discretion**: the model policy no longer sets `effort` values,
  and `--variant` is only passed when a spawner explicitly requests one.

## Claude Code cross-session message passing — investigation

Claude Code >=2.1.224 added a native cross-session messaging feature:

- **`SendMessage` tool**: a Claude Code session can message any other session, on any machine, addressed
  by name; **`ListAgents`** discovers them (macOS/Linux). Sends are permission-classified in auto mode.
- **Settings**: `crossSessionInbound` and `dialogExpiry` control whether messages to a session running
  with bypassed permissions are held for approval vs auto-delivered.
- 2.1.225: `SendMessage` can start a conversation with Remote Control sessions on other machines
  (`ListAgents` shows `name [ref]`).

Relevance to Styloagent: this is a *native* inter-agent transport that could replace or complement the
git-backed Signal Bus for Claude-family agents. It is Anthropic-account-scoped (sessions must share an
account/org) and has no durable channel trace of its own — Styloagent's bus remains the audit record.
Current integration uses the bus + hooks delivery; native SendMessage is a possible future delivery
path for `claude`-runtime agents, but it is not wired in this change. The fleet's own
`send_message`/`check_inbox`/`reply_to_thread` MCP verbs remain the coordination contract for all
runtimes, including kilo.
