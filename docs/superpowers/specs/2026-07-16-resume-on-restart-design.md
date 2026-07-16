# Resume-on-restart — fleet continuity across crash & rebuild (spec ①)

- **Date:** 2026-07-16
- **Owner:** `overview-` (architect). Implementation spans **`session-`** and **`bus-`**.
- **Status:** Design — agreed in brainstorm, pending spec review → `writing-plans`.

## Context & motivation

Styloagent agents dogfood Styloagent: an agent fixes a bug in the app, builds a fixed binary, and must
**transition into that binary without losing its working context**. Today that loop is manual and
lossy — fix → merge → build → *quit → relaunch → re-explain everything to a cold agent*. The same gap
bites on any crash: the fleet comes back empty and every agent has to be re-briefed.

This spec is **the foundation** that closes the gap: on crash or restart, the fleet comes back with
continuity. It is deliberately scoped to *resume*; the agent-triggered **build transition**
(`transition_build` — spec ②) is a thin orchestration layer built on top and depends on this landing
first.

## Goal

After an app crash or restart:

- **Active agents resume their actual Claude sessions automatically** — no manual step, no re-briefing.
- **Parked agents wake on demand** — only when a bus message is waiting for them.

So no working context is lost, startup cost is bounded to what's actually in flight, and "you"
(`overview-`) come back able to keep going.

## Decisions (locked in brainstorm)

1. **Resume unit:** native `claude --resume <session_id>` is primary; the context-doc cold-start is the
   fallback.
2. **Two-tier resume:** eager resume of the **active** fleet on startup; **lazy wake-on-inbox** for the
   **inactive** fleet.
3. **Active/inactive line:** *active* = a live Claude session (`working | idle | needs-you`); *inactive*
   = `dehydrated`/parked. (`idle` resumes eagerly — a live session with warm context is worth
   `--resume`ing. Rejected alternative: demote `idle` to lazy — more economical, but throws away warm
   sessions.)
4. **Startup is automatic** for the active tier — no confirmation prompt (that is what makes the loop
   hands-free).

## What already exists (this is mostly wiring, not new machinery)

- **Claude `session_id` captured** from every hook event — `AgentPaneViewModel.cs:208`
  (`if (!string.IsNullOrEmpty(e.SessionId)) _sessionId = e.SessionId;`). Currently in-memory only.
- **Compact/resume re-anchoring** — `HydrationText.For(...)` re-injected by the `SessionStart` hook when
  `source=compact|resume`. Fires automatically on `--resume`.
- **Dehydrate → `SavedContextPath` doc → rehydrate** cold-start path (the fallback unit already exists).
- **Live roster** — `Panes` → `BuildFleetSnapshot()`.
- **Inbox-scanning delivery** — `ChannelDeliveryCoordinator` / `MessageDeliveryService` already scan
  inboxes and do idle-gated, at-least-once delivery (ack = observed side-effect).

## Design

### A. The live-fleet manifest

`.styloagent/live-fleet.json` — a **runtime sidecar**: gitignored, never committed (honours
*presentation-state-is-a-sidecar; never mixed into the channel*). Per agent it holds enough to
reconstruct the pane and resume it:

`prefix`, `parentPrefix`, `depth`, `responsibility`, `colour`, `repoRoot`, `worktreePath`,
`worktreeBranch`, `launchPromptPath`, `savedContextPath`, `lastSessionId`, `lastState`, `lastUpdated`.

- **Write cadence:** eager and **debounced (~1s)** on every meaningful change — session-id
  first-seen/changed, spawn, dehydrate, exit, worktree change. A crash never runs a graceful shutdown
  handler, so the file *is* the crash-survivable truth.
- **Atomic write:** temp file + rename, so a crash mid-write never corrupts it.
- Essentially persists the roster we already build in memory plus the session id we already capture.

### B. Startup resume — active tier (eager)

On launch, at project attach: load the manifest; for each agent with `lastState ∈ {working, idle,
needs-you}`:

1. Recreate its pane (reuse `CreatePaneForProposed`, same lineage/colour/worktree from the manifest).
2. Launch with **`claude --resume <lastSessionId>`** instead of a cold prompt (keeping the existing hook
   + MCP args).
3. The `SessionStart source=resume` hook re-injects `HydrationText` → the agent re-anchors identity and
   re-reads its context doc automatically.

Relaunches are **staggered/throttled** (small concurrency cap + delay) to avoid a spawn storm — and the
spawns must run **off the UI thread** (see the git-fork-on-UI-thread freeze lesson,
`cockpit-freeze-git-subprocesses-fork-on-the-ui-t`). Each resume is logged to the timeline.

### C. Lazy wake-on-inbox — inactive tier

Parked/dehydrated agents from the manifest are recreated as **ghost panes** (dock slot kept, no PTY).
The delivery coordinator — which already scans inboxes — gains one step:

- **On startup:** scan each parked agent's inbox (`channel/inbox/<prefix>*.md` + `channel/inbox/all-*.md`).
  If it has waiting/unread entries → **wake** that agent.
- **Post-startup:** a *new* message addressed to a parked agent → the same wake.
- **Wake =** `--resume` (or cold-start from `savedContextPath`), then the message delivers idle-gated as
  usual — *revive-then-deliver*, one added step in the existing delivery path.

Startup cost is bounded to the active set; every parked agent costs nothing until it has mail. This is
attention-first applied to revival: an agent wakes exactly when it is needed.

### D. Fallback & failure handling

`--resume` is best-effort. **Failure detection:** the resumed process exits within ~5s without a valid
session, or errors → fall back to the **cold-start** (fresh `claude` reading `savedContextPath` / the
restart prompt = today's rehydrate). Worst case degrades to today's behaviour — never a hang. Each
agent's outcome (`resumed | cold-started | skipped | failed`) lands in the timeline, so it's observable,
never silent.

### E. Scope & boundaries

- **Resumed (eager):** active-tier agents, **including `overview-`** (a fresh `overview-` re-anchors from
  its context doc — it does *not* restore the exact prior chat; that is the accepted nature of
  context-doc continuity).
- **Lazy (wake-on-mail):** dehydrated/parked agents.
- **Untouched:** `exited`/crashed agents stay exited and re-spawnable (per the fleet-respawn fix,
  `4bb1713`); agents whose worktree no longer exists are skipped with a timeline note.

### F. Invariants preserved

- Filesystem + git remain the source of truth; the manifest is a **rebuildable projection**, never a
  dependency.
- **Degrade-never-destroy:** a missing/corrupt manifest ⇒ a cold cockpit (exactly today's behaviour),
  not a failure.
- Sidecar: the manifest never mixes into channel files.
- The single-rooted authority tree is unchanged — resume recreates the same lineage recorded in the
  manifest.

### G. Ownership seam

- **`session-`:** manifest read/write, startup active-tier resume, PTY relaunch with `--resume`, the
  fallback.
- **`bus-`:** the wake-on-inbox trigger inside the delivery coordinator (scan parked inboxes;
  revive-then-deliver).
- They coordinate over the bus; `overview-` holds the seam and re-derives the architecture if it shifts.

## Risks & de-risking

- **R1 (critical — spike FIRST).** Does `claude --resume <session_id>` reliably restore a session whose
  PTY was killed **mid-turn** by a crash? The plan's first task is a spike confirming: (a) the hook
  `session_id` is exactly what `--resume` accepts; (b) session files survive a crash; (c) how `--resume`
  behaves on an interrupted/mid-turn session; (d) per-cwd vs global session storage. **If unreliable,
  the context-doc fallback becomes primary — the two-tier design still stands, it just leans on the
  safety net.**
- **R2.** Startup fork storm → throttle relaunch and keep spawns off the UI thread.
- **R3.** Very stale session → fallback covers it; optional soft age cap.
- **R4.** Worktree removed since crash → skip + timeline note.

## Testing

- **Unit:** manifest round-trip (serialise/deserialise, field fidelity); the active/inactive state
  filter; debounce coalescing; the fallback decision.
- **Seam:** a fake launcher asserts a Live agent → `--resume <id>` args; a failed resume → cold-start
  fallback; a parked agent + an inbox entry → wake-then-deliver. Reuses the existing fake-PTY/launcher
  patterns.

## Out of scope (separate specs)

- **② agent-triggered build transition** (`transition_build` MCP verb): snapshot the fleet → launch the
  freshly-built bundle → exit the old process → the new binary auto-resumes via this spec. Depends on
  this.
- Deep multi-repo workspace resume beyond per-agent `repoRoot`.
