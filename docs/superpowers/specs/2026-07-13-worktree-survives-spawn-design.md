# Worktree Survives Spawn — Design

**Date:** 2026-07-13
**Status:** Design approved; ready for implementation planning
**Scope:** Carry the overview/architect's per-agent **worktree** decision from `proposed-agents.yaml`
through the human **Spawn** action, by unifying the two spawn paths onto one governed path.
Discovered from the inside: the `overview-` agent hit this while running the real spec→shape→fleet
loop — it is told to decide `worktree: true/false` per agent, but the proposal schema has nowhere to
put the decision and the roster-spawn path never acts on it.

---

## 1. The problem

There are **two spawn paths, and only one carries worktree:**

- **Agent / MCP path** — `spawn_agent(…, worktree)` → `SpawnRequest(… bool Worktree)` →
  `MainWindowViewModel.SpawnChild` → creates the worktree (`:1117-1128`), governor-checked. ✅
- **Human / PROPOSED-roster path** — click **Spawn** → `ProposedTeamViewModel.Spawn` →
  `MainWindowViewModel.SpawnProposed` → `CreatePaneForProposed` with **no worktree override**
  (`:1096-1103`), and **no governor check**. ❌

Consequences:

1. A proposed agent the human spawns is **always in-repo** — the architect's isolation judgment has
   nowhere to be recorded (`ProposedAgent` / the YAML row have no `worktree` field) and nowhere to
   act (`SpawnProposed` ignores it).
2. The roster path **bypasses the governor** — a human can spawn past `fleet-full` / `max-depth` /
   `paused` from the roster, while an agent calling `spawn_agent` cannot. A latent asymmetry.

The architect *is* instructed to make the call: `DefaultTemplates.cs:85-88` documents the
`spawn_agent` `worktree` parameter — but the `proposed-agents.yaml` schema block right above it
(`:54-60`) omits the field. Contract and capability disagree.

## 2. Approach — unify onto one governed path (Approach B)

Route the normal human-spawn through the same `SpawnChild` the MCP path uses, so **one path** carries
governor + worktree + lineage. Thread a `worktree` bit from the YAML into it. Make governor
rejections visible on the roster (the price of putting the human click behind the governor).

### 2.1 Schema & contract (data model)

- **`ProposedAgent`** (`src/Styloagent.Core/Projects/ProposedAgent.cs`) — add a 5th positional
  member `bool Worktree = false`. Defaulted, so the two positional constructions
  (`ProposedAgentsReader:35`, `MainWindowViewModel:1130`) keep compiling. Non-breaking.
- **`ProposedAgentRow`** (`ProposedAgentsReader.cs`) — add `public bool Worktree { get; set; }`.
  VYaml's `[YamlObject]` default naming is LowerCamelCase, so the YAML key is `worktree`. **Absent →
  `false`**, so every existing `proposed-agents.yaml` and `team.yaml` stays valid (the committed team
  travels through the same reader).
- **`ProposedAgentsReader.Read`** — pass `r.Worktree` into the `ProposedAgent`.
- **Teach the field** — add `worktree: false` with a one-line comment to the schema block in **both**
  `DefaultTemplates.SystemPrompt` (`:54-60`) and the live `.styloagent/system-prompt.md`. These two
  files duplicate the architect's contract (see §5); both must learn the field or the architect won't
  emit it. The comment mirrors the `spawn_agent` rule: *set `true` only when this agent's work
  overlaps files another agent owns.*

### 2.2 The unified spawn path

- **Extract** `SpawnChild`'s worktree-add block (`:1117-1128`) into a private helper
  `TryAddWorktree(string prefix, out string? path, out string? branch, out string? error)`. One
  implementation of worktree creation, two callers. Behavior identical to today for `SpawnChild`.
- **`SpawnProposed(ProposedAgent p)` returns `SpawnOutcome`:**
  - **Normal case** — an overview owner exists (`owner is not null && owner.Prefix != p.Prefix`):
    build `SpawnRequest(owner.Prefix, p.Prefix, p.Responsibility, p.Dir, p.LaunchPrompt, p.Worktree)`
    and call `SpawnChild(req)`. Governor + worktree + parent/depth lineage, one path. **This is the
    unification** and it closes the §1.2 asymmetry.
  - **Root / no-owner exception** — no overview present (a bare worktree roster), or the proposal *is*
    the root. `FleetGovernor.Check` is parent-centric (`parent is null → Deny UnknownParent`), and
    forcing a root through it would either always fail or invite a **second root**, breaking the
    single-rooted authority invariant (`lint_authority`, commit `a6e8a52`). So this branch stays a
    direct `CreatePaneForProposed`, but now **honors `p.Worktree`** via the shared `TryAddWorktree`
    helper. This is the *one* deliberate exception; it establishes/uses the single root and is
    documented as such in the code.

### 2.3 Human-click semantics & error handling (B's real behavior change)

Putting the human click behind the governor means a click can now be **rejected**. That must be
visible, not silent.

- The Spawn callback type changes `Action<ProposedAgent>` → `Func<ProposedAgent, SpawnOutcome>`
  (`MainWindowViewModel.SpawnProposed` supplies it at `:872`).
- **`ProposedTeamViewModel.Spawn`** — remove the card **only** on `outcome.Spawned`. On rejection,
  keep the card and set a new `ProposedAgentItem.RejectionMessage` (e.g. `fleet full (12/12)`,
  `paused`) rendered on the card in red.
- **`ProposedTeamViewModel.SpawnAll`** — iterate; spawn what is allowed, and leave rejected cards in
  place with their reasons. Do **not** abort the batch on the first rejection.

Everything degrades the Styloagent way: a failed worktree add returns a `SpawnOutcome.Reject`
(existing `SpawnChild` behavior) rather than throwing; the durable files are untouched.

## 3. Data flow (after)

```
proposed-agents.yaml (worktree: true)
   → ProposedAgentsReader.Read → ProposedAgent{ Worktree = true }
   → ProposedTeamViewModel card (shows an "isolated" intent)
   → click Spawn
   → MainWindowViewModel.SpawnProposed
        owner exists?  ── yes ─→ SpawnRequest{ Worktree } → SpawnChild → FleetGovernor.Check
                                     → TryAddWorktree → CreatePaneForProposed(worktreeOverride)
                                     → SpawnOutcome
                        ── no  ─→ (root exception) TryAddWorktree → CreatePaneForProposed
   → outcome.Spawned ? remove card : keep card + RejectionMessage
```

## 4. Testing

- **`ProposedAgentsReaderTests`** — `worktree: true` parses to `Worktree == true`; a fixture with the
  key **absent** parses to `false` (backward-compat); existing fixtures still pass unchanged.
- **Headless `MainWindowViewModel` / `ProposedTeamViewModel` test** (fake `IGitService`):
  - A proposed agent with `Worktree == true` triggers `AddWorktreeAsync` and the resulting pane has a
    non-null `WorktreePath`.
  - A governor rejection (fleet **paused**, or **fleet-full**) leaves the card present with a
    `RejectionMessage` set and creates **no** pane.
  - `Worktree == false` still produces an in-repo pane (no `AddWorktreeAsync` call).
- Existing `FleetGovernor` tests already cover the deny reasons; no change there.

## 5. Out of scope (named, not done)

- **Doc-duplication root cause** — `DefaultTemplates.SystemPrompt` and `.styloagent/system-prompt.md`
  carry the same architect contract; this design updates both copies but does not de-duplicate them.
- **Palette legibility** — `agent_color` returning a contrast-checked set for N prefixes.
- **Living-architecture ergonomics** — `architecture_impact` defaulting `before` to the on-disk
  `architecture.md`; an author-time C4 lint.

These are separate friction points found in the same session; each warrants its own small fix.
