# MCP Fleet — recursive self-assembly via `spawn_agent`

**Status:** Design — pending approval
**Date:** 2026-07-10
**Author:** Styloagent

---

## 1. Goal

Turn the overview agent from a *suggester* into an *assembler*. Styloagent hosts an in-process
MCP server exposing `spawn_agent` and `list_fleet`; the overview (and every child) calls
`spawn_agent` to launch its own subsystems, which recurse and specialise. Spawning is
**autonomous** — a child launches immediately, no human click — but **bounded** by a fleet
governor (max fleet size, max depth) and a global **Pause** switch in the cockpit.

This is the spec's parked **Theme 4 (MCP)** slice. It builds directly on the orchestration
bootstrap (`2026-07-10-orchestration-bootstrap-design.md`): that slice launches the overview and
renders a human-gated PROPOSED section; this slice lets agents spawn directly, in real time.

**Runtime intent it serves:** the overview determines shape and the initial subsystems; those
subsystems keep splitting and specialising. The bootstrap delivered *suggest*; this delivers
*assemble + recurse*.

---

## 2. Scope

**In scope**
- An in-process **HTTP MCP server** (localhost, ephemeral port) started with the app.
- Two tools: `spawn_agent(prefix, responsibility, dir, launchPrompt)` and `list_fleet()`.
- Per-agent `--mcp-config` carrying the caller's identity, so spawns are **parented**.
- A pure **`FleetGovernor`** enforcing `MaxFleet` / `MaxDepth` / `Paused` / duplicate-prefix /
  invalid-prefix, returning structured rejections (never exceptions).
- Parent/depth tracking on every pane; roster shown as a **depth-indented tree** with a header
  HUD (`fleet N/max · depth d/max`) and a **⏸ Pause fleet** toggle.
- Spawned children are real roster agents: launched with hooks + mcp-config + system prompt,
  persisted to the manifest.
- Default system prompt updated to teach agents the two tools and the split discipline.

**Out of scope (later slices)**
- Per-agent git worktrees (children reuse the bootstrap's dir resolution).
- MCP `send_message` / `update_status` (the file-drop bus and hook-state channel already cover
  inter-agent messaging and status).
- A `despawn` / kill verb (Pause covers the immediate safety need; despawn is a fast-follow).
- Auth beyond loopback + a per-run bearer token; remote / multi-machine fleets.

---

## 3. Transport & identity

Claude Code accepts MCP servers via `--mcp-config <json>`. Each agent we launch receives a config
naming Styloagent's server as an **HTTP** server, with the agent's identity in a header:

```json
{
  "mcpServers": {
    "styloagent": {
      "type": "http",
      "url": "http://127.0.0.1:<PORT>/mcp",
      "headers": {
        "X-Styloagent-Agent": "<prefix>",
        "Authorization": "Bearer <per-run-token>"
      }
    }
  }
}
```

The server reads `X-Styloagent-Agent` per request to identify the **caller by prefix** (the parent
of any spawn) and validates the bearer token. Prefixes are unique among live agents (the
`DuplicatePrefix` guard enforces this), so a prefix is a stable identity — no separate id scheme is
introduced. The hook id remains the launcher's concern for the hook-state channel; MCP identity is
the prefix.

**Plan-time verification (required, like `--append-system-prompt` was in the bootstrap):** confirm
the exact `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` package names + versions and
that Claude Code's `--mcp-config` accepts an inline JSON `http` server with custom headers. If
inline JSON is not accepted, write the config to a temp file and pass its path. If custom headers
are not forwarded, fall back to a per-agent URL path (`/mcp/<prefix>`) for identity.

---

## 4. Components / files

**Core (create) — pure, app-agnostic, fully unit-testable:**
- `Mcp/FleetGovernor.cs` — `Decision Check(FleetState state, string parentPrefix, string newPrefix)`
  where `FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused)`
  and `Decision` is `Allowed` or `Rejected(RejectReason, string message)`. Reasons: `FleetFull`,
  `MaxDepth`, `Paused`, `DuplicatePrefix`, `InvalidPrefix`, `UnknownParent`. Depth of a new child =
  parent's depth + 1 (the overview is depth 0). No I/O.
- `Mcp/SpawnRequest.cs` — `sealed record SpawnRequest(string ParentPrefix, string Prefix, string
  Responsibility, string Dir, string LaunchPrompt)`.
- `Mcp/SpawnOutcome.cs` — `sealed record SpawnOutcome(bool Spawned, string? AgentId, RejectReason?
  Reason, string Message)` with `static Ok(string id)` / `static Reject(RejectReason, string)`.
- `Mcp/FleetSnapshot.cs` — `sealed record FleetMember(string Prefix, string Responsibility, string?
  ParentPrefix, int Depth, string State)`; `sealed record FleetSnapshot(IReadOnlyList<FleetMember>
  Members, int MaxFleet, int MaxDepth, bool Paused)`.
- `Mcp/IFleetController.cs` — the app seam: `Task<SpawnOutcome> SpawnAsync(SpawnRequest req)`;
  `FleetSnapshot Snapshot()`. Implemented in App; faked in tests.
- `Mcp/FleetPolicy.cs` — `sealed record FleetPolicy(int MaxFleet, int MaxDepth)` + a tolerant VYaml
  reader `FleetPolicyReader.Read(string path)` returning defaults (`MaxFleet=12, MaxDepth=3`) on
  missing/invalid, never throwing (mirrors `ProposedAgentsReader`).

**Core (modify):**
- `Projects/ProjectScaffolder` + `ProjectConfig` — scaffold `.styloagent/fleet.yaml` with the
  default policy (idempotent, never overwrites an edited file); add `FleetPolicyPath` to
  `ProjectConfig`.
- `Projects/DefaultTemplates.SystemPrompt` — teach the two tools + the split discipline
  (`list_fleet` before spawning to avoid dups; split until subsystems are focused; spawns may be
  rejected when the fleet is full — adapt, don't retry blindly).

**App (create):**
- `Mcp/StyloagentMcpServer.cs` — owns the Kestrel host + MCP endpoint; registers the two tools
  wired to an injected `IFleetController`; exposes `Uri BaseUrl`, `string Token`, and
  `IReadOnlyList<string> McpConfigArgs(string prefix)` (builds the `--mcp-config` args for a
  launch). `StartAsync` / `DisposeAsync`.
- `Mcp/McpConfig.cs` — builds the config JSON (§3) for a given prefix, url and token.
- `Services/FleetController.cs` — implements `IFleetController`; holds a reference to the
  `MainWindowViewModel` (or a narrow interface onto it); marshals `SpawnAsync` to the UI thread,
  consults `FleetGovernor` against the live roster, calls the pane-creation path with a parent
  link, returns the `SpawnOutcome`; `Snapshot` projects the roster to a `FleetSnapshot`.

**App (modify):**
- `ViewModels/AgentPaneViewModel.cs` — carry `string? ParentPrefix`, `int Depth`, `string
  Responsibility`.
- `ViewModels/MainWindowViewModel.cs` — (a) start/own the `StyloagentMcpServer` and the
  `FleetController`; (b) append `mcpServer.McpConfigArgs(prefix)` to **every** agent launch (overview
  + children) alongside `HookArgs` and the system-prompt args; (c) a `SpawnChild(SpawnRequest)` path
  that reuses the existing `SpawnProposed`/`AddAgent` pane creation but records parent + depth; (d)
  expose `int FleetCount`, `int MaxFleet`, `int MaxDepth`, `int CurrentDepth`, `bool FleetPaused`
  and a `PauseFleetCommand`.
- `Views/AgentsView.axaml` — depth-indent roster rows by `Depth`; add a header HUD
  (`fleet N/max · depth d/max`) and a **⏸ Pause fleet** toggle bound to the VM.

**App (config):**
- Add the required package references (`ModelContextProtocol.AspNetCore` + ASP.NET Core /
  `Microsoft.AspNetCore.App` framework reference) to `Styloagent.App`.

---

## 5. Data flow

```
app start → StyloagentMcpServer up on 127.0.0.1:PORT (token minted)
project open → ProjectScaffolder.Ensure (now also fleet.yaml) → policy loaded into FleetGovernor
  → overview launched: HookArgs + McpConfigArgs(overview-) + --append-system-prompt
  overview: list_fleet() → [overview-] → spawn_agent(foss-, …)
    → server (header prefix=overview-, token ok) → SpawnRequest(parent=overview-)
    → FleetController.SpawnAsync → UI thread → FleetGovernor.Check(state, overview-, foss-)
    → Allowed → create pane(parent=overview-, depth=1) → launch claude
       (HookArgs + McpConfigArgs(foss-) + launchPrompt) → persist manifest → SpawnOutcome.Ok(id)
  foss-: spawn_agent(nuget-) → depth 2 → … → MaxFleet/MaxDepth → SpawnOutcome.Reject(FleetFull)
  human ⏸ Pause → governor.Paused=true → subsequent spawns Reject(Paused)
```

---

## 6. Error handling

- All governor decisions are **structured tool results** the calling agent can read (`"rejected:
  fleet full (12/12)"`), never thrown exceptions.
- Server binds loopback only, ephemeral port, per-run bearer token; requests with a bad/missing
  token or unknown `X-Styloagent-Agent` are refused.
- If the server fails to start (e.g. port clash after retries), agents launch **without** the
  mcp-config (no-spawn degrade) and the cockpit surfaces a non-fatal warning; the rest of the app
  is unaffected.
- Every app mutation (pane creation, roster changes) is marshalled to the UI thread inside
  `FleetController`.
- `spawn_agent` is guarded against duplicates (same prefix already live → `DuplicatePrefix`) and
  malformed prefixes (`InvalidPrefix`), so a confused agent can't corrupt the roster.

---

## 7. Testing

- **`FleetGovernor` (pure):** allow under limits; `FleetFull` at `MaxFleet`; `MaxDepth` beyond the
  cap; `Paused` when paused; `DuplicatePrefix` for a live prefix; `UnknownParent` for a missing
  parent id. Full coverage, no I/O.
- **`FleetPolicyReader`:** parses `fleet.yaml`; defaults on missing/invalid; never throws.
- **Tool handlers (fake `IFleetController`):** `spawn_agent` maps `SpawnOutcome` → tool result
  (Ok/Reject text); `list_fleet` serialises the snapshot. No Kestrel.
- **`FleetController` (App, headless dispatcher):** `SpawnAsync` creates a pane with the right
  parent/depth and returns `Ok`; consults the governor (rejects when full/paused); `Snapshot`
  reflects the live roster.
- **Light integration:** start the server, assert `tools/list` returns the two tools, and a
  `tools/call spawn_agent` drives a fake controller end-to-end over HTTP. **No real `claude`
  spawned** — consistent with the existing suite.
- **Scaffolder idempotence:** `fleet.yaml` written with defaults; a second `Ensure` does not
  overwrite an edited policy.

---

## 8. Resolved decisions

- **Autonomy:** autonomous spawn + guardrails (immediate launch, bounded by `MaxFleet`/`MaxDepth`,
  global Pause). Not human-gated.
- **Transport:** in-process **HTTP** MCP server (localhost, ephemeral port), official
  `ModelContextProtocol.AspNetCore`. Identity via request header, validated by a per-run token.
- **Tools:** `spawn_agent` + `list_fleet` only. Messaging/status stay on the existing bus + hooks.
- **Guardrail defaults:** `MaxFleet=12`, `MaxDepth=3`, in `.styloagent/fleet.yaml` (user-tunable);
  Pause is a runtime toggle.
- **Worktrees:** children reuse the bootstrap's dir resolution; per-agent git worktrees deferred.
- **Kill:** no `despawn` verb this slice; Pause is the safety control.
