# Agent Log — per-agent streaming transcript log (design)

**Status:** approved (overview- + operator, 2026-07-17)
**Owner of design:** `overview-`. Implementation is sliced per owning agent (see §6).

## Goal

Every agent window gets a **persistent, timestamped markdown log of its conversation turns**, appended
live, stored one file per agent in a directory, indexed by the **existing** document search, and openable
from the pane's queued **dropdown/zoom** control as "log for THIS agent."

Inspiration: a timestamped per-conversation message log. The value over the live terminal: the log is
**durable and keyed per-agent, not per-session** — it survives dehydrate → rehydrate → re-spawn and
terminal-scrollback loss, so you can review or search a *parked ghost's* whole history.

## Why this shape

It is mostly **wiring**, not new infrastructure — Styloagent already has the four moving parts:

- the transcript reader (`Core/Transcripts/TranscriptReader`) — the source of turns;
- the markdown Document Library + **Lucene** search — indexes and searches markdown;
- the rendered-markdown viewer (LucidView) — the open-as-rendered-markdown affordance (capability #9);
- the per-pane dropdown/zoom control (queued for cockpit-) — the surface.

Writing the log **as markdown in a directory** is the load-bearing decision: it makes the log "just
another markdown-backed document," so search, autosuggest, and the rendered viewer get it for free.

## Shape / data model

**Storage:** one file per agent, keyed by prefix, under a runtime directory:
`\.styloagent/logs/<prefix>.md` (gitignored runtime state, alongside `channel/` and `issues/` — a sidecar,
never mixed into channel files). Keyed by **prefix** (not session id) so it spans the agent's whole life.

**Format** — human-readable, greppable, renders clean, Lucene-tokenizes well:

```markdown
# Agent log — session-

## 2026-07-17 01:47:30 · assistant
<turn text>

## 2026-07-17 01:48:12 · user
<turn text>

---
<!-- re-spawn 2026-07-17 02:10:04 -->

## 2026-07-17 02:10:20 · assistant
<turn text>
```

- One `##` block per completed turn: `<ISO-ish timestamp> · <role>` heading + the turn's message text.
- v1 logs the **message text** of assistant/user turns. Tool calls are out of scope for v1 (a compact
  one-line tool summary is a possible phase-2 enrichment — YAGNI for now).
- A `---` + `<!-- re-spawn … -->` marker separates lifecycle sessions within the one per-agent file.

## Data flow

```
claude transcript (JSONL, per session)
        │  new completed turn (turn-boundary signal)
        ▼
AgentLogWriter (session-)  ──cursor per agent──►  append markdown block
        │
        ▼
.styloagent/logs/<prefix>.md   ──indexed by──►  Lucene doc search (repo-)
        │                                                   │
        └────────── opened by ──────────►  rendered-markdown viewer, from the
                                            pane dropdown "Log (this agent)" (cockpit-)
```

## Slices (one per owner)

1. **Log writer — `session-`** (`Core` — a new `AgentLogWriter` near `Core/Sessions`/`Core/Transcripts`,
   session-'s domain). On each turn boundary — the `Stop` hook session- already receives, or a light
   transcript-file watch — project the newly-completed transcript turn(s) into timestamped markdown and
   **append** to `\.styloagent/logs/<prefix>.md`. Maintain a per-agent cursor (last-projected turn) so it
   is incremental and idempotent. On re-spawn, append after a lifecycle separator (never overwrite).
2. **Index/search — `repo-`** (`Core/Docs` + Lucene). Add `\.styloagent/logs/` to the Lucene index roots
   so the existing document search + autosuggest see the logs. They are markdown ⇒ already handled;
   incremental re-index as files grow (the index already tolerates changing docs).
3. **View — `cockpit-`** (`Styloagent.App` pane UI). Add a "Log (this agent)" entry to the queued
   dropdown/zoom pane control that opens `\.styloagent/logs/<selectedPrefix>.md` in the standard
   rendered-markdown viewer (the same open-as-rendered-markdown-in-a-new-dock-document gesture).

## Invariants / error handling

- **Degrade, never destroy.** The log is a *derived projection*; the JSONL transcript stays the source of
  truth. A writer failure (unreadable transcript, disk error) is best-effort: trace + skip, never crash or
  stall the agent. Losing the log never loses the conversation.
- **Sidecar.** The log dir is per-instance runtime state (gitignored `\.styloagent/logs/`); it never mixes
  into the shared channel files, and closing Styloagent leaves the transcripts intact.
- **Idempotent + incremental.** The per-agent cursor means re-projecting is a no-op; the file only grows.

## Testing

- **Writer:** a transcript with N turns → N timestamped markdown blocks in the right order; a second pass
  appends only new turns (cursor); a simulated re-spawn appends after a separator and does not overwrite;
  an unreadable transcript ⇒ no throw, no partial-corrupt file (best-effort).
- **Index:** a file under `\.styloagent/logs/` is discoverable via the existing doc search.
- **View:** the pane dropdown opens the log for the *selected* agent (correct prefix), rendered as markdown.

## Build order

1. **Writer** (session-) — the core value; produces the markdown files. TDD.
2. **Index** (repo-) — point Lucene at the logs dir (depends on 1 producing files; small).
3. **View** (cockpit-) — dropdown entry → viewer (depends on 1; independent of 2).

## Ownership / dogfooding

Cross-cutting but **cleanly separable**: each slice lands entirely in its owner's domain — session-
(Sessions/Transcripts/Hooks writer), repo- (Docs/Lucene index roots), cockpit- (pane dropdown). No
cross-owner edits are required, so the ownership gate holds naturally (no leases/prods needed). Like all
runtime code, the writer + view take effect for the fleet only after a **cockpit rebuild + restart**
(see `[[cockpit-runtime-changes-need-restart]]`); unit-test each slice out-of-process meanwhile.
