# Spec-First Orchestration — Design

**Status:** draft for review

## The idea

When Styloagent opens a project, the **overview/architect agent** doesn't jump to spawning a fleet.
It works in three layers, top-down:

```
1. SPEC   — what is this system? (the source of truth everything else derives from)
2. SHAPE  — the architecture that realises the spec (C4, ownership-coloured)
3. FLEET  — the agents that build/own the shape (each owns a component)
```

Each layer is a **living document** the overview agent curates; lower layers are re-derived when an
upper layer changes. Sub-agents feed understanding back up over the bus.

## Layer 1 — Spec (the new first layer)

The overview agent's **first job** is to produce `.styloagent/spec.md` — a concise description of the
system: purpose, users, core capabilities, constraints, and the "shape of the problem". Two entry
paths:

- **New system** (empty project, `.styloagent/brief.md` present): the overview agent runs a
  **"tell me about the system"** dialogue — researches comparable systems, then asks the human
  clarifying questions *one at a time* to scope it appropriately (target users, must-have-now vs
  later, constraints, tech, scale). It writes the agreed understanding to `spec.md`. *(The brief we
  already seed instructs exactly this.)*
- **Existing system** (has a README / docs / code): the overview agent **starts exploring** — reads
  the README, `docs/`, entry points, and git history — and drafts `spec.md` from what it finds,
  asking the human only to fill genuine gaps. Code investigation is driven **from the spec's
  questions**, not a blind full-repo scan.

Gate: the human confirms `spec.md` before the shape is derived. This is where scope is pinned.

## Layer 2 — Shape (architecture)

From the confirmed spec, the overview agent designs the **C4 architecture** and writes
`.styloagent/architecture.md` (a ```mermaid C4Component``` block). Each component is coloured by its
intended owning agent via `UpdateElementStyle($bgColor=…)`. Styloagent renders it live and clickably
(the native `C4Canvas` + routing + click-to-focus we built). Each *proposal* to change the shape
shows its impact via `C4Diff` (`+ Component / − path / Impact:`).

## Layer 3 — Fleet

From the shape, the overview agent decides the initial **3–4 agents** — one per top-level component /
responsibility — and writes `.styloagent/proposed-agents.yaml`. The human reviews and spawns them
(existing flow: PROPOSED roster → Spawn). Each agent's identity colour is the colour of its component
in the C4, so the architecture doubles as the ownership map. Agents may split further via
`spawn_agent`, bounded by `fleet.yaml`.

## The feedback loop

As sub-agents work, they learn the real system and report back over the **bus** (now with priority
delivery). The overview agent folds that back into `spec.md` → re-derives `architecture.md` → adjusts
the fleet. The three docs stay a **live projection of the design conversation**, not stale artifacts.

## What already exists (reuse)

- New-system **brief** + overview **system prompt** (`DefaultTemplates`) — already tell the architect
  to research → clarify → define shape → build the first feature.
- **C4 pipeline** — `ParseAndLayoutC4`, native `C4Canvas` (colour + click), routing, `C4Diff`,
  `C4ResponsibilityGenerator`, `ShowArchitecture` command.
- **Bus + priority delivery**, **proposed-agents** roster + `spawn_agent`, **fleet policy**.
- `ProjectConfig` paths for the `.styloagent/` artifacts.

## What to build (implementation plan)

1. **Spec artifact** — add `SpecPath` (`.styloagent/spec.md`) to `ProjectConfig`; scaffold an empty
   spec; a "Spec" doc tab / panel (reuses `MarkdownDocumentView`).
2. **Overview prompt: make the 3 layers explicit + sequential** — update `DefaultTemplates.SystemPrompt`
   and the brief so the architect (a) writes `spec.md` first and waits for confirmation, (b) then
   `architecture.md`, (c) then `proposed-agents.yaml`. Existing-project path: "explore README/docs
   first, draft the spec, ask only for gaps".
3. **Spec → Shape derivation cue** — a lightweight signal/command so, once the human confirms the
   spec, the overview agent is prompted to (re)derive the architecture.
4. **Living-docs refresh** — on bus messages that change understanding, the overview re-derives; show
   the `C4Diff` impact of each change in the architecture panel.
5. **(Later)** A guided "system setup" surface in the cockpit that walks Spec → Shape → Fleet with
   confirm gates, rather than it being purely prompt-driven.

## Open questions

1. **Spec confirmation** — explicit human gate (a "Confirm spec" action) or just conversational
   ("looks right?") in the terminal?
2. **Existing projects** — auto-start exploration on open, or wait for the human to say "tell me about
   the system"?
3. **Who writes the C4** — the overview agent authors `architecture.md` by hand, or does it emit a
   structured model that `C4ResponsibilityGenerator` renders? (The generator gives consistent
   ownership colours for free.)
