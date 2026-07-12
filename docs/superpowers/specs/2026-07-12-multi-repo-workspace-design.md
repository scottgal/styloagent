# Multi-Repo Workspace — Design Spec

**Status:** Draft for review · 2026-07-12

**Goal:** Let one Styloagent cockpit orchestrate a fleet that works across **several repos at once**
(the user routinely works across ~4), coordinated by a **workspace overview** agent, with each agent
visibly **coloured by its repo**.

**Core idea — the specialist team travels with the repo.** Each repo carries its *own committed*
`.styloagent/` context (spec, architecture, the proposed/defined fleet, protocol). When you check out
or clone a repo into the workspace, Styloagent **picks that context up and instantiates the specialist
team defined for that stack** — no re-onboarding. The fleet is a portable, versioned property of the
repo (think "devcontainer, but for the agent team"): clone the repo, get its team; the team evolves in
git alongside the code.

**Motivating use case (real):** In a single session an agent worked in `styloagent` *and* its testing
framework `lucidRESUME` — editing the framework, publishing a NuGet, then consuming it back in
styloagent. Today that's two disconnected single-repo cockpits. This spec makes it one workspace where
a workspace overview drives per-repo fleets over one shared bus, each repo bringing its committed team.

---

## 1. Decisions (locked)

- **Integrated workspace**, not federated instances: one Styloagent process opens a *workspace* of N
  repos; per-repo fleets run under a single workspace overview; **one shared bus**.
- **Repo hue families**: each repo gets a base hue; agents in that repo take identity colours from
  that hue family, so the repo is glanceable in the roster, tabs and timeline.

## 2. Architecture

```
Styloagent (one cockpit, one workspace)
  └─ workspace-  (workspace overview: reads every repo, routes cross-repo work)
       ├─ repo A (blue)   ── overview-A + foss-A, test-A …   worktrees in A
       ├─ repo B (green)  ── overview-B + ui-B, api-B …      worktrees in B
       └─ repo C (amber)  ── overview-C + docs-C …           worktrees in C
                    ▲
              ONE shared workspace bus (send_message, addressed by prefix)
```

- **Repo-bound agents.** Every agent belongs to exactly one repo; its git worktree and cwd are in that
  repo. Cross-repo work happens by agents in different repos *coordinating over the shared bus* — never
  one agent editing two repos.
- **Workspace overview (`workspace-`).** A top-level agent above the repo overviews. It reads each
  repo's `spec.md`/`architecture.md`, decides cross-repo work (e.g. "bump the framework in B, then
  consume it in A"), and routes tasks to the relevant repo overview via `send_message`. It is the
  human's single point of contact for the whole workspace.
- **Per-repo overviews (`overview` per repo).** Unchanged from today's single-repo overview, one per
  repo, each owning its repo's spec → architecture → fleet. They report up to the workspace overview.

## 3. Components (what to build)

### 3.1 Workspace config — `Styloagent.Core.Workspace`
A `.styloagent-workspace/workspace.yaml` at a chosen workspace root:

```yaml
name: mostlylucid
repos:
  - path: ~/RiderProjects/styloagent
  - path: ~/RiderProjects/lucidRESUME
overview:
  systemPromptPath: .styloagent-workspace/workspace-overview.md   # optional; a default is scaffolded
```

- `WorkspaceConfig` record: `Name`, `IReadOnlyList<RepoRef> Repos`, `WorkspaceRoot`, `ChannelRoot`
  (the shared bus, `<workspaceRoot>/.styloagent-workspace/channel`), `OverviewSystemPromptPath`.
- `RepoRef` record: `Path` (absolute), `Name` (dir name, unique within the workspace), `Index` (stable
  order → drives the hue).
- `WorkspaceStore` (VYaml, mirrors `RecentProjectsStore`/`PreferencesStore`): sync `Load(path)` +
  `SaveAsync`. **Reuse the sync-load lesson** — never `LoadAsync().GetResult()` on the UI thread.
- `WorkspaceScaffolder.Ensure(root)`: creates `.styloagent-workspace/` (workspace.yaml + shared
  channel + a default workspace-overview prompt), and calls the existing per-repo `ProjectScaffolder`
  for each repo. Backward compatible: opening a *single repo* (no workspace.yaml) is a workspace of one.

### 3.2 Shared workspace bus
- The channel moves up to the **workspace** level: `<workspaceRoot>/.styloagent-workspace/channel`.
  Every agent's `--mcp-config` points its `send_message` at this one channel, so all agents (workspace
  overview + every repo's agents) address each other by prefix on one bus. (Per-repo `.styloagent/channel`
  is retained only for the single-repo/back-compat path.)
- `ChannelMessageWriter`, `ChannelProjection`, `ChannelDeliveryCoordinator` are **already channel-root
  agnostic** — they take a `channelRoot`. `MainWindowViewModel._channelRoot` becomes the workspace
  channel. No change to the send/deliver mechanics; only which root is passed.

### 3.3 Agent identity across repos
- **Prefixes are unique within the workspace.** The spawn path (workspace overview + `spawn_agent`)
  guarantees uniqueness; on a collision the repo name is used as a disambiguator suffix
  (`foss-` in two repos → `foss-` and `foss-lucid-`). Simpler than namespacing every address.
- **`AgentManifestEntry` and `AgentPaneViewModel` gain a `Repo` field** (the owning `RepoRef.Name`).
  `Worktree`/cwd already point into that repo.

### 3.4 Repo hue families — `Styloagent.Core.Presentation.RepoPalette`
- A pure `RepoPalette`: `Hue BaseHueFor(int repoIndex)` from a fixed list of well-separated base hues
  (blue, green, amber, teal, rose, purple, slate…, cycling). `string AgentColor(int repoIndex, int
  agentIndex)` returns a hex that keeps the repo hue but varies lightness/saturation per agent, so
  agents in a repo are the same family, distinguishable from each other.
- Replaces `PresentationStore.DefaultColorFor(prefix)` with a repo-aware `ColorFor(repoIndex, prefix)`.
  Existing per-project `PresentationStore` (explicit per-agent colours) still overrides when set.

### 3.5 MCP surface additions (for the workspace overview / orchestrator)
Extending the fleet-control tools already built (`fleet_status`, `read_timeline`, `who_touched`,
`recent_files`, `search_docs`, `dehydrate_agent`, `read_agent`, …):
- `list_repos()` — the workspace's repos: name, path, base-hue, live-agent count. The orchestrator's map.
- `fleet_status()` / `AgentStatus` gain a **`repo`** field, so status is grouped/filterable by repo.
- `spawn_agent(...)` gains a **`repo`** param — which repo the child works in (defaults to the caller's
  repo). The workspace overview uses it to place specialists in the right repo.
- `who_touched(path)` / `recent_files(limit)` already key on absolute paths, so they span repos
  unchanged; the result gains the repo name for clarity.
- `search_docs(query)` indexes **all repos' docs** (the `DocumentSearchIndex` builds over every repo
  root + the shared channel); each hit gains its repo.

### 3.6 UI
- **Roster grouped by repo.** `AgentsView` gets a per-repo section (repo-hue header: name + agent
  count), agents nested under their repo. Collapsible per repo.
- **Repo-coloured everywhere.** Tabs, timeline rows, instruments already read `BorderColorHex` — now
  that colour comes from the repo hue family, so repo reads at a glance with no new chrome.
- **Repo filter.** A top-bar/roster filter to focus one repo (hide the others' panes) when the
  workspace is large. Layout modes (tabs/tile/auto-tile) operate on the filtered set.
- **Instruments** show per-repo counts (e.g. `A: 3 · B: 2`).

## 4. Data flow — a cross-repo task

1. Human tells `workspace-`: "bump the UITesting framework in lucidRESUME to add X, then use it in
   styloagent."
2. `workspace-` calls `list_repos()` + `search_docs("uitesting release")`, reads the relevant docs.
3. `workspace-` `send_message(to="overview-lucid", …)` → the lucidRESUME overview drives its fleet to
   make + publish the change; reports back over the shared bus.
4. `workspace-` waits (polling `fleet_status()` / `read_agent`), then `send_message(to="overview-sty",
   …)` to consume the new version in styloagent.
5. Every hop is on the shared bus + the activity timeline (repo-coloured), so it's fully auditable.

## 5. Onboarding — pick up a repo's committed team

Adding a repo to the workspace (open a folder, or clone one in) runs a **detect → instantiate _or_
scaffold** flow per repo:

- **Repo already has a committed `.styloagent/`** → *pick it up*: read its `spec.md`,
  `architecture.md`, and its committed fleet definition, and offer that repo's **defined specialist
  team** as a PROPOSED section (repo-hue coloured), ready to spawn — no re-onboarding. This is the
  headline: clone a repo, immediately get the team that ships with it.
- **Repo has no `.styloagent/`** → *scaffold*: the current path — run `ProjectScaffolder`, launch that
  repo's overview, let it research the stack and propose a team, which (once the human approves) gets
  **committed** into the repo so next checkout picks it up.

Committed fleet definition: today the team is proposed into `proposed-agents.yaml`. For portability,
promote the agreed team to a committed **`.styloagent/fleet.yaml`** (prefix, responsibility, dir,
launch prompt, worktree flag per agent) — the source of truth a checkout instantiates. `system-prompt.md`,
`PROTOCOL.md`, `architecture.md`, `spec.md`, `fleet.yaml` are **committed**; runtime state
(`channel/`, `launch-prompts/` scratch, saved-context checkpoints, `.styloagent/shots/`) stays
**git-ignored** (the repo already `.gitignore`s runtime state — extend it).

Workspace entry points:
- `STYLOAGENT_WORKSPACE=<path>` opens a workspace directly (parallel to today's `STYLOAGENT_REPO`).
- Open a folder with `.styloagent-workspace/workspace.yaml`, or pick several repo folders → a
  `workspace.yaml` is scaffolded from them.
- Launch `workspace-` with the workspace-overview prompt; each repo's overview + committed team surface
  under it (one PROPOSED section per repo).

**Back-compat:** opening a single repo (no workspace.yaml) behaves exactly as today = a workspace of
one repo, no separate workspace overview (the repo overview *is* the top), and if that repo has a
committed `.styloagent/fleet.yaml` its team is picked up.

## 6. Testing

- `WorkspaceStore` round-trip (sync `Load` + async) — like `PreferencesStoreTests`.
- `fleet.yaml` round-trip + **pickup**: a repo with a committed `fleet.yaml` instantiates exactly that
  team; a repo without one falls to scaffold.
- `WorkspaceScaffolder.Ensure` idempotency + per-repo scaffold; committed vs. git-ignored split is correct.
- `RepoPalette`: distinct base hues per repo index; agents within a repo share the hue, differ per
  agent; determinism.
- Prefix-uniqueness/disambiguation across repos.
- `search_docs` spans repos (index built over multiple roots).
- Cross-repo routing: a message from `workspace-` to `overview-<repo>` resolves + delivers.

## 7. Phasing (→ implementation plan)

1. **Workspace model + committed-team pickup** — `WorkspaceConfig`/`RepoRef`/`WorkspaceStore`/
   `WorkspaceScaffolder`; promote the fleet to a committed `.styloagent/fleet.yaml` + a reader; the
   detect→instantiate-or-scaffold flow per repo; `Repo` field on manifest + pane; back-compat
   single-repo. (This phase alone delivers "clone a repo → get its team" for one repo.)
2. **Repo colour** — `RepoPalette` hue families; repo-aware colours; roster grouped by repo; tabs/
   timeline/instruments repo-coloured.
3. **Shared bus** — channel at the workspace level; every agent's MCP config points there; verify
   cross-repo `send_message`.
4. **Workspace overview** — the `workspace-` agent + its system prompt; per-repo overviews under it;
   routing; PROPOSED-per-repo onboarding.
5. **MCP additions** — `list_repos()`, `repo` on `fleet_status`/hits, `spawn_agent(repo)`,
   `search_docs` across repos; document them in the workspace-overview system prompt.
6. **UI polish** — repo filter; per-repo instruments; layout modes over the filtered set.

## 8. Open questions

- **Worktree strategy across repos** — each repo manages its own worktrees (existing per-repo git
  policy); the workspace doesn't add a cross-repo worktree concept. Confirm.
- **Shared vs. per-repo protocol** — one workspace `PROTOCOL.md` vs. per-repo. Proposal: one workspace
  protocol (coordination is workspace-wide) + each repo keeps its architecture docs.
- **Router/test-slots** across repos — the router (SSH/test-slot spec) is workspace-scoped already
  (envs, not repos); no change expected.
