# Agent Runtime Hooks Design

**Status:** implemented for runtime selection, mixed spawning, and Codex hook/MCP parity for the
Styloagent cockpit lifecycle.

## Goal

Styloagent should treat "Claude" and "Codex" as agent runtimes behind one cockpit lifecycle:

- create a pane
- launch a CLI in a PTY
- inject the launch or restart prompt
- observe state transitions
- deliver fleet/MCP work
- dehydrate and rehydrate through durable context docs

The extension point is not the button. It is the runtime profile that translates Styloagent's common
agent lifecycle into the selected CLI's native command, flags, hook mechanism, permissions, and MCP
configuration.

## Current Runtime Contract

`AgentRuntimeProfile` owns:

- `Kind`: stable persisted runtime id (`claude`, `codex`)
- `Command`: executable launched in the PTY
- `SupportsClaudeSettingsHooks`: whether the runtime can receive the current `claude --settings` hook/MCP flags
- `PermissionArgs(mode)`: runtime-native permission flags

Runtime-specific hook builders live beside the profile:

- `HookSettings`: Claude `--settings <json>` hook, permission, delivery and hydration builder
- `CodexHookSettings`: Codex `--config hooks.Event=[...]` hook builder

Claude currently supports the full contract:

- `--settings <json>` hook drop files
- hydration on `SessionStart` compact/resume
- ownership gate on `PreToolUse`
- priority delivery on `UserPromptSubmit` and `Stop`
- `--mcp-config <json>` for Styloagent MCP tools
- `--append-system-prompt <text>` for overview agents

Codex supports:

- `codex` PTY launch
- launch/restart prompt injection through the same terminal path
- permission-mode mapping with Codex-native flags
- inline Codex hook config via repeated `--config hooks.Event=[...]` arguments
- shared hook drop files for `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`,
  `PermissionRequest`, `PreCompact`, `PostCompact`, `SubagentStart`, `SubagentStop`, and `Stop`
- hydration on `SessionStart` compact/resume through Codex `additionalContext` output
- ownership gate denial on `PreToolUse` through Codex `permissionDecision = deny`
- priority delivery on `UserPromptSubmit` and `Stop`, including `Stop` continuation via
  Codex `decision = block`
- Codex MCP registration via `--config mcp_servers.styloagent...`, including per-agent auth headers
- overview/developer instructions through Codex `developer_instructions`

Codex deliberately does not receive Claude-only flags. Passing `--settings`, `--mcp-config`, or
`--append-system-prompt` to Codex would make spawn reliability depend on flags the Codex CLI does not
advertise.

## Hook Adapter Target

A runtime reaches full Styloagent parity when its adapter can emit the common `HookEvent` stream:

- session start/end
- user prompt submitted
- pre/post tool use with tool name and target
- notification or permission prompt requiring the operator
- pre-compaction
- turn stop / idle boundary

The adapter may be implemented as CLI config, a wrapper process, an MCP bridge, or native runtime
hooks. The rest of the cockpit should consume only normalized `HookEvent` records and should not care
which agent runtime produced them.

## Remaining Follow-Up

The remaining gaps are intentionally narrow:

- map more Codex permission payload variants if the CLI adds new shapes
- evaluate whether scoped Claude permission commands need a stricter Codex-specific policy mapping
- add real CLI smoke coverage once the test harness can launch Codex non-interactively in CI
