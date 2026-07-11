# Git Client Integration — Design

**Date:** 2026-07-11
**Status:** Approved (design), pending implementation plan
**Author:** overview / architect (with human)

## Goal

Give Styloagent a **full, in-app git client** — commit graph, file diffs, stage/commit,
push/pull, branch, stash — driven per agent, by **reusing SourceGit's Avalonia controls**
rather than building the hard visual pieces from scratch. Tie **git worktrees to agents**
(each agent optionally isolated on its own worktree/branch) and handle **agent wrap-up**
(gated auto-merge to main + worktree cleanup).

Terminal and git are the two first-class surfaces of the cockpit: a terminal *is* an agent,
a worktree *is* an agent's isolated workspace, and the git panel is how the human sees and
finishes an agent's work.

## Why SourceGit

[SourceGit](https://github.com/sourcegit-scm/sourcegit) is an open-source cross-platform git
GUI client. It is a near-perfect donor:

- **MIT licensed** — we may vendor/fork its code with attribution.
- **Same stack as Styloagent:** Avalonia + `CommunityToolkit.Mvvm`.
- **Same git backend approach:** shells out to the `git` CLI (needs git ≥ 2.25.1), exactly
  like our existing `GitCliReader` — no `LibGit2Sharp`.
- **Version-aligned:** SourceGit targets **net10.0, Avalonia 11.3.18, CommunityToolkit.Mvvm
  8.4.2**; Styloagent is net10.0 / Avalonia 11.3.12 / CTMvvm 8.4.0. A minor bump closes the
  gap; their source compiles in our tree nearly as-is.

The one caveat: SourceGit is a monolithic **application** assembly, not a control library.
Its controls are coupled to its own ViewModels/Models/Commands and a forked AvaloniaEdit; its
top-level Repository view further assumes app-level singletons (Preferences, its popup/
notification system, locale dictionaries). We therefore reuse its *controls and git layer*,
not its *app shell*.

## Chosen approach — A: Vendor into `Styloagent.Git`

Three approaches were considered:

- **A. Vendor the needed SourceGit source into a new `Styloagent.Git` project** and build our
  own agent/worktree-aware repository panel on top. **(Chosen.)**
- **B. Fork SourceGit, split a `SourceGit.Controls` library, submodule + reference it.** Keeps
  an upstream-sync path but requires refactoring their monolith and decoupling controls from
  App singletons first — more upfront and ongoing merge work.
- **C. Embed SourceGit's whole Repository view wholesale.** Least code, but drags their App
  resources/locales/popup/Preferences singletons into our app (two `App.axaml` worlds) — the
  brittle trap.

**A is chosen** because it is the only option that delivers a full client *and* the tight
agent/worktree/wrap-up integration this feature is about, without inheriting SourceGit's app
shell. Divergence from upstream is acceptable: we are building a product, not tracking their
releases. Every vendored file keeps its MIT header; `Styloagent.Git/THIRD-PARTY.md` records the
provenance and commit SHA we vendored from.

### What we vendor (reuse) vs. what we build (own)

| Reuse (copy from SourceGit, adapt namespaces) | Own (write, agent/worktree-aware) |
|---|---|
| `Commands/*` — git-CLI wrappers (log, diff, status, stage, commit, push, pull, branch, stash, worktree) | `IGitService` — the seam our VMs call; wraps the vendored commands, marshals threading |
| `Models/*` — `Commit`, `Change`, `Branch`, `Worktree`, graph model | `WorktreeAgentMap` — binds a worktree to its owning agent (prefix + colour) |
| Commit-graph control (custom `Control` that draws the history lane graph) | `RepositoryPanelViewModel` — per-agent repo state (graph, changes, diff, actions) |
| Diff view (AvaloniaEdit-based, syntax + inline/side-by-side) | `GitPanelView` + sub-views — hosts the vendored controls in our shell/theme |
| AvaloniaEdit fork (as a git submodule under `external/`) | `WrapUpService` — the gated auto-merge + cleanup state machine |

Anything touching SourceGit's `Preferences`, popup/notification system, locale (`Models.Locale`),
or `App.*` is **not** vendored; those call-sites are replaced with our equivalents or removed.

## Architecture & module boundaries

```
Styloagent.Git (new project)
├── vendored/                      # SourceGit source, MIT, namespaces rewritten to Styloagent.Git.Vendored
│   ├── Commands/                  # git CLI wrappers
│   ├── Models/                    # Commit, Change, Branch, graph model
│   └── Controls/                  # CommitGraph control, Diff view
├── GitService.cs                  # implements Styloagent.Core.Git.IGitService over vendored Commands
├── WrapUpService.cs               # gated auto-merge + cleanup state machine
└── THIRD-PARTY.md                 # attribution + vendored SHA

external/AvaloniaEdit              # submodule (SourceGit's fork), referenced by Styloagent.Git

Styloagent.Core.Git (existing)
├── IGitReader / GitWorktree / GitCliReader   # keep — worktree *detection* already used at startup
├── IGitService (interface)                    # NEW: full git operations seam (impl in Styloagent.Git)
├── WorktreePolicy.cs                          # NEW: reads .styloagent/git-policy.yaml
└── GitPolicy.cs                               # NEW: record (TestCommand, removeWorktreeOnMerge, mainBranch)

Styloagent.App
├── ViewModels/GitPanelViewModel.cs            # NEW: owns RepositoryPanelViewModel per worktree/agent
├── Views/GitPanelView.axaml                   # NEW: hosts vendored controls, our theme tokens
├── Mcp/FleetTools.cs                          # spawn_agent gains `worktree` param; new wrap_up tool
└── ViewModels/MainWindowViewModel.cs          # spawn→worktree create; wrap-up trigger; git tab
```

`Styloagent.Git` depends on Avalonia + the AvaloniaEdit submodule; `Styloagent.Core` stays
UI-free and only owns the `IGitService` interface, `GitWorktree`, and policy records. This keeps
the vendored (heavier) code isolated in one project the rest of the app references.

## Worktree ⇄ agent lifecycle

### 1. Create (on spawn)

`spawn_agent(prefix, responsibility, dir, launchPrompt, worktree)` gains a boolean `worktree`.

- The **overview agent decides** `worktree`: it sets `true` when it assesses the new agent's
  responsibility **overlaps files an existing agent owns** (collision-isolation). Non-overlapping
  agents share the repo root (`worktree: false`). The system prompt documents this rule.
- `worktree: true` →
  `git worktree add <repo>/.worktrees/<prefix> -b agent/<prefix>` (branch name de-duplicated),
  then launch claude with that worktree as its working dir. Falls back to a clear rejection if
  the repo is dirty in a way that blocks the add, or the path exists.
- The worktree inherits the agent's **identity colour** (`PresentationStore.DefaultColorFor`)
  so it reads as that agent's lane in the graph — consistent with the C4 ownership colouring.

`.worktrees/` is git-ignored in the target repo (Styloagent adds it to `.git/info/exclude` if
absent, so we never dirty the user's `.gitignore`).

### 2. Work

The git panel (below) shows the selected agent's worktree: its branch, the history graph, the
working-tree changes, and per-file diffs. The agent commits from its terminal or via the panel's
stage/commit controls. Ahead/behind vs. `main` and dirty/clean/conflicted status show as badges
on the roster and in the panel.

### 3. Wrap-up (gated auto-merge + cleanup)

Wrap-up is a **deliberate action**, not inferred from idle: either the human clicks *Wrap up* on
the agent, or the agent calls a new `wrap_up()` MCP tool to signal it is done. There is no
implicit "agent went Idle → merge".

`WrapUpService` then runs a state machine:

1. **Guard clean:** working tree must be committed (no uncommitted changes). If dirty → abort,
   keep worktree, tell the human what's uncommitted.
2. **Run tests:** run `GitPolicy.TestCommand` (from `.styloagent/git-policy.yaml`) in the
   worktree. No command configured → skip with a visible warning ("wrap-up merged without tests").
3. **Merge attempt:** `git checkout main && git merge --no-ff agent/<prefix>`.
   - **Tests green + merge clean** → keep merged, `git worktree remove` the worktree, delete the
     branch, retire the agent from the roster. This is the happy-path "auto".
   - **Tests red** OR **merge conflict** → `git merge --abort` (if needed), **keep the worktree**,
     and **file an issue** via the Issues feature (`IssueStore.Write`, reporter = `wt-<prefix>`,
     severity `high`, title e.g. *"wrap-up blocked: tests failed on agent/foss-"*, detail = the
     failing output / conflicting paths). The agent stays live for the human/triage to resolve.

So "auto-merge + cleanup" is automatic on the happy path but **never merges broken or conflicting
code silently** — failure degrades to keep-worktree + a triage issue. This reuses the Issues
system we just shipped and previews the eventual GitHub-triage integration.

`git-policy.yaml` (all optional, sensible defaults):

```yaml
testCommand: dotnet test        # run before merge; omit to skip (with warning)
removeWorktreeOnMerge: true     # remove the worktree after a clean merge
mainBranch: main                # merge target; auto-detected if omitted
```

Wrap-up is always the gated auto-merge described above; there is no menu mode in this cut (a
`GitPolicy` record leaves room to add one later without a schema break).

## Git panel UX

A new **Git** surface in the cockpit, peer to the terminal. Selecting an agent (or its roster
row) focuses its worktree in the panel. Layout (vendored controls in **bold**):

```
┌ Git · wt-foss-  (agent/foss-  ↑3 ↓0  ✎ dirty) ──────────────────────────┐
│ ┌ HISTORY ─────────────┐ ┌ CHANGES ────────┐ ┌ DIFF ──────────────────┐ │
│ │ **commit-graph**     │ │ M src/Foo.cs     │ │ **AvaloniaEdit diff**  │ │
│ │  main ●──●──●         │ │ A src/Bar.cs     │ │  - old line            │ │
│ │   foss ●──●  (colour) │ │ ? notes.md       │ │  + new line            │ │
│ │                      │ │ [stage] [stage ∀]│ │  (syntax highlighted)  │ │
│ └──────────────────────┘ └──────────────────┘ └────────────────────────┘ │
│ [commit…]  [push ↑] [pull ↓] [branch ⑂] [stash]      [ Wrap up ▸ ]        │
└───────────────────────────────────────────────────────────────────────────┘
```

- **History:** vendored commit-graph, coloured by owning agent where a lane maps to a worktree.
- **Changes:** working-tree file list with stage/unstage.
- **Diff:** vendored AvaloniaEdit diff for the selected file.
- **Actions bar:** commit, push, pull, branch, stash (our VMs → `IGitService` → vendored commands).
- **Wrap up:** triggers `WrapUpService` for this agent (confirmation shows what will run: tests,
  merge target, worktree removal).

Placement follows the existing shell: the Git panel is a document/tab peer to agent terminals
(reuses the Dock layout), and the roster gains per-agent git badges (ahead/behind, dirty/clean/
conflict) so status is visible without opening the panel. Honours the app's light/dark theme
tokens (`ThemeTokens.axaml`); vendored controls are restyled to our tokens, not SourceGit's.

## Data flow & threading

```
UI (GitPanelView) ──binds──► GitPanelViewModel / RepositoryPanelViewModel
                                   │  calls
                                   ▼
                            IGitService (Styloagent.Core.Git)
                                   │  impl
                                   ▼
                            GitService (Styloagent.Git)
                                   │  uses
                                   ▼
                     vendored Commands  ──► `git` CLI (async process)
```

- All git operations are **async** (process-based, like `GitCliReader`) and run off the UI
  thread; results marshal back via `Dispatcher.UIThread` (mirrors `FleetController`).
- `IGitService` is the only seam the App/VMs touch, so the vendored code stays swappable and
  testable behind an interface.
- File-watch: the panel refreshes on a debounced `FileSystemWatcher` over the worktree's `.git`
  (HEAD/index) so external commits (the agent's own, via its terminal) reflect promptly.

## Error handling

- Git command failures never throw across the seam: `IGitService` returns typed results
  (`GitResult<T>` with success + stderr), and the panel surfaces the stderr inline (matching the
  tolerant, never-throw style of `GitCliReader`).
- Wrap-up failures are first-class (kept worktree + filed issue), not exceptions.
- Worktree-add on a conflicting path / dirty repo → rejection surfaced to the caller
  (`spawn_agent` returns a `rejected: …` string, same pattern as governor rejections).

## Testing

- **Unit (Core / Git):** command-output parsers (log→graph model, `status --porcelain`→changes,
  `worktree list`→worktrees) with fixture strings — no live git. `WrapUpService` state machine
  driven by a fake `IGitService` + in-memory issue store: assert green→merge+remove, red→keep+
  issue, dirty→abort, conflict→keep+issue.
- **Integration (opt-in):** a temp git repo (init, commit, `worktree add`) exercising
  `GitService` end-to-end, gated so CI without git skips gracefully.
- **Headless render (UITests):** `GitPanelView` bound to a fixture repository VM renders the
  graph + a diff; screenshot capture (mirrors `IssuesViewTests` / `DocLibraryViewTests`).
- **MCP:** `spawn_agent` honours `worktree`; `wrap_up` routes to `WrapUpService` (fake controller,
  like `FleetToolsTests`).

## Out of scope (now) / future

- **GitHub triage integration:** external issues → triage agent → fleet, building on the Issues
  `Source`/`Status` fields and this wrap-up→issue path. Explicitly future.
- Interactive rebase, cherry-pick UI, blame, submodule management inside the panel — vendor later
  if wanted; not in the first cut.
- Multi-repo / non-worktree remotes beyond the project's own repo.

## Licensing

SourceGit is MIT. Every vendored file retains its copyright header; `Styloagent.Git/THIRD-PARTY.md`
records SourceGit's licence text, the upstream repo, and the exact commit SHA vendored from. The
AvaloniaEdit fork is added as a submodule under `external/` with its own licence intact.
