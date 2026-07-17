# Ownership Enforcement Gate — Design / Scoping

**Status:** draft for review (scoped by overview- while session- finishes Fix 3)
**Issue:** `enforce-ownership-boundaries-a-cross-owner-file`

## Goal

Make agent ownership boundaries **enforced**, not voluntary. *"If it touches someone else's file it
needs a prod."* An agent that tries to write a file owned by another agent is **blocked** and told to
coordinate through the owner / overview before proceeding. This is the layer that makes the C4
ownership map *real* and stops the collisions we hit this session (session- vs cockpit- on the build
fix) from being possible at all.

## The problem (recap)

Today boundaries are enforced only by (a) launch-prompt scoping and (b) the advisory `who_touched()`
query — nothing *stops* a cross-owner edit. docs- respecting cockpit-'s files was voluntary; session-
reaching into `ArchitectureImpact.cs`/`App.csproj` was the same rule *not* holding. The encoded
PROTOCOL rule (STOP + route through overview) helped the second time — but it relies on the agent
choosing to obey. This makes it structural.

## Design

### 1. Ownership map — file → owning agent

Need file-level ownership. The C4 maps *components* → owners; the file-level projection lives in an
explicit, overview-maintained manifest so the architecture stays the human map and the manifest is its
machine-readable form:

```yaml
# .styloagent/ownership.yaml  (maintained by overview-, derived from architecture.md)
owners:
  cockpit-:  [src/Styloagent.App/**]
  session-:  [src/Styloagent.App/Views/AgentPaneView*, src/Styloagent.App/Services/PtyMessageInjector.cs,
              src/Styloagent.Core/Hooks/**, src/Styloagent.Terminal/**]
  bus-:      [src/Styloagent.Core/Channel/**, src/Styloagent.Core/Mcp/**, src/Styloagent.App/Mcp/**]
  repo-:     [src/Styloagent.Core/Git/**, src/Styloagent.Git/**, src/Styloagent.Core/Docs/**]
# most-specific glob wins; unlisted paths are UNOWNED (shared, editable by anyone)
```

- **Resolution:** longest/most-specific matching glob wins (so session- can own a file *inside*
  cockpit-'s `src/Styloagent.App/**`).
- **Unowned = shared** (tests, docs, new files) — don't over-block.
- Pure, testable resolver: `OwnershipMap.OwnerOf(path) -> prefix?`.

### 2. Enforcement — a PreToolUse hook

Styloagent already injects hooks per spawned agent and receives them over its local socket (§4.4).
Add a **PreToolUse** hook on `Edit` / `Write` / `NotebookEdit`:

1. Extract the target path from the tool input.
2. `owner = OwnershipMap.OwnerOf(path)`.
3. ALLOW if: `owner == caller`, or `owner == null` (unowned), or caller holds a valid **lease** (§3),
   or caller is `overview-` (coordination root).
4. Otherwise **BLOCK** — return a deny decision whose reason is the *prod instruction*:
   > `src/…/Foo.cs is owned by cockpit-. Do not edit it. Coordinate: send_message overview- (or cockpit-) to request a lease, or hand the change to the owner.`

Reads (`Read`/`Grep`/`Glob`) are never gated — ownership gates **writes** only. (Phase 2 may extend to
`Bash` mutations like `git`/`rm` on owned paths.)

### 3. The "prod" — leases (self-service coordination)

So a cross-owner edit is *possible* when the owner agrees, without a human:

- **Lease store** `.styloagent/leases.yaml`: `{ path-glob, grantedTo, grantedBy, expiresAt }`.
- MCP verbs: `request_lease(path, reason)` (messages the owner + overview) and
  `grant_lease(path, toPrefix, ttl)` (owner or overview writes the lease). The PreToolUse hook honors
  an unexpired lease.
- Leases **expire** (TTL) so authority doesn't leak. Revocable by owner/overview.

**v1 can ship without full leases:** the gate BLOCKS + emits the prod; the overview resolves by either
doing the edit, reassigning ownership, or (minimal) `grant_lease`. Full self-service leases = phase 3.

### 4. Escape hatches / rules

- `overview-` bypasses (it's the coordination root and maintains the map).
- Unowned paths, `obj/bin/`, and gitignored paths are never gated.
- An agent always owns files under its own dedicated worktree that aren't in another agent's globs.
- Fail-open on hook error (never hard-block an agent because the gate crashed — degrade, never destroy).

### 5. Where it lives (and who'd own each piece)

| Piece | Component / owner |
|---|---|
| `OwnershipMap` model + resolver, `ownership.yaml` | Core; **overview-** owns the manifest, model is shared/bus- |
| Lease store + `request_lease`/`grant_lease` MCP verbs | `Core/Mcp` + `App/Mcp` → **bus-** |
| PreToolUse hook injection + socket handler (allow/block) | `Core/Hooks` + spawn wiring → **session-** |
| Cockpit surface (show ownership + active leases; grant from UI) | `App` → **cockpit-** |

Ironically cross-cutting — so the overview owns the *design* + the manifest; implementation is sliced
per owner and coordinated (dogfooding the very rule).

## Build order (slices)

1. **Manifest + resolver** (Core, pure, TDD). `ownership.yaml` seeded from `architecture.md`.
2. **PreToolUse gate**: hook on Edit/Write → resolver → block cross-owner with the prod message.
   Delivers the core enforcement (blocks; overview resolves manually).
3. **Leases**: store + `request_lease`/`grant_lease` verbs + hook honors leases → self-service.
4. **Cockpit UI + `Bash`-mutation gating + who_touched cross-check + expiry/revoke**.

Slice 2 is the MVP that would have prevented today's collision.

## Key decisions — RESOLVED (overview-, human-delegated 2026-07-16; recommended defaults adopted)

1. **Ownership source:** ✅ explicit `.styloagent/ownership.yaml` — **written** (Slice 1 done), derived
   from `architecture.md`, maintained by overview-. Not C4-derived-at-runtime, not `who_touched`-emergent.
2. **v1 depth:** ✅ ship **Slice 2 only** first (gate blocks cross-owner writes + emits the prod;
   overview resolves manually). Leases (Slice 3) follow once Slice 2 is proven.
3. **Unowned default:** ✅ **shared/editable** — unlisted paths are not gated (don't over-block).
4. **Scope:** ✅ gate **`Edit`/`Write`/`NotebookEdit` only** for v1; `Bash` mutations deferred to Slice 4.

> Any of these can be overridden by the human later; recorded here so implementation isn't blocked.

## Status / next

- Slice 1 (manifest + this decision set) — **done** by overview-.
- **`OwnershipMap.OwnerOf` resolver — DONE** (overview-, 2026-07-17): `src/Styloagent.Core/Ownership/OwnershipMap.cs`
  — pure, never-throws, most-specific-glob-wins, loads `.styloagent/ownership.yaml` via VYaml. 11 TDD tests
  green (`tests/Styloagent.Core.Tests/OwnershipMapTests.cs`), including the carve-out-beats-broad-owner
  headline case, backslash/`./` normalisation, and invalid-YAML→Empty (degrade-never-destroy). On main.
- **Slice 2 (PreToolUse gate) — IN PROGRESS (session-).**
  - **Decision core DONE** (`c4a986e`): `OwnershipGate.Decide(caller, tool, path) -> Allow | Deny+prod`
    composes `OwnershipMap` with all §4 rules — writes-only (Edit/Write/NotebookEdit; Read/Grep/Glob/Bash
    pass), overview- bypass, exemptions (tests/docs/.styloagent/obj/bin), unowned⇒allow, owner==caller⇒allow,
    cross-owner⇒deny+prod, and FAIL-OPEN (never throws). 23 TDD tests; Core 300/300. Lives in Core/Hooks.
  - **TRANSPORT CORRECTION (supersedes the §4.4 "local socket" assumption).** The running hook transport is
    an *observational file-drop* (`<hooksDir>/<id>__<uuid>.json`, consumed async by `HookChannel` for status
    badges) — it structurally CANNOT block. A PreToolUse gate must return the deny decision **synchronously**
    on stdout before the tool runs, so it needs a NEW synchronous path (the observational drop stays for badges).
  - **MECHANISM — GREENLIT: "gate-mode"** (overview-, 2026-07-17). The App exe short-circuits at
    `Program.Main` (BEFORE Avalonia) when invoked as a hook: `OwnershipGateCli` parses the PreToolUse event
    JSON, loads `<root>/.styloagent/ownership.yaml`, runs `OwnershipGate`, emits the standard PreToolUse deny
    JSON for a cross-owner write (else nothing). Chosen over a cockpit round-trip because the safety gate must
    NOT depend on cockpit liveness — a frozen/closed cockpit must never stall or disable edits (degrade-never-
    destroy) — and gate-mode is faster (no Avalonia) with no new project. *Future (non-blocking): split a
    trimmed gate CLI if per-edit spawn latency bites.*
  - **DOGFOODING:** wiring the gate needs cockpit-'s files (`Program.cs` early-exit branch + threading the
    repo root into the hook-settings call site in `MainWindowViewModel.cs`). session- correctly did NOT edit
    them (the rule working before it's enforced) and routes the exact diff through overview-, who applies it
    (coordination-root bypass).
  - **LANDED + VERIFIED** (2026-07-17): `OwnershipGateCli` (session-, `6ca3f55`) + the App wiring
    (overview-, `2329027`: `Program.Main` gate-mode short-circuit + `HookArgs` passes gate invocation/root/
    caller-prefix). Smoke-tested end-to-end against the BUILT exe in gate-mode, 5/5: session-→cockpit- file =
    DENY+prod; session-→its own `PtyMessageInjector` carve-out = ALLOW (most-specific glob wins, live);
    overview-→cockpit- = ALLOW (bypass); session- Read = ALLOW (reads never gated); bus-→cockpit- = DENY.
    Core 322/322. **ACTIVE for agents spawned from now on** (the hook is injected at spawn — agents already
    live this session are not retro-gated). Remaining: live-agent confirmation (session-, after rehydrate) —
    a freshly spawned gated agent actually blocked in its PTY — then close `enforce-ownership-boundaries`.
    Deferred: Slice 3 (leases + `request_lease`/`grant_lease`), Slice 4 (cockpit UI, `Bash`-mutation gating);
    documented residuals from session-'s security pass (`b17cad7`): case-insensitive-FS + symlink traversal.
