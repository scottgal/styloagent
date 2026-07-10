# Orchestration Bootstrap — point at a directory → launch overview → propose the team

**Status:** Design — pending approval
**Date:** 2026-07-10
**Author:** Styloagent

---

## 1. Goal

The tool's **first-run / setup flow**. Point Styloagent at a project directory; it loads (or
scaffolds) a system prompt + coordination protocol, launches a single **overview agent** in the
folder, and that agent analyses the system's shape and **proposes the initial 3-4 subsystem
agents**. The human reviews the proposal and spawns each with a click.

This is the spec's parked **"P4 orchestration bootstrap / initial ingest"** sub-project, scoped to
the *entry + suggest* half (no MCP yet — the overview *suggests*; the human spawns).

**Runtime intent it serves (context, not built here):** the overview determines shape and the
initial 3-4 subsystems; those subsystems then keep splitting and specialising. That recursion lives
in the *system prompt + PROTOCOL* and (later) the MCP spawn verb — not in Styloagent code.

---

## 2. Scope

**In scope**
- Welcome screen: open a project folder + recent projects.
- `.styloagent/` project config, scaffolded-if-missing (system prompt, PROTOCOL, channel).
- Launch the `overview-` agent with the system prompt injected, in the project folder.
- Watch `.styloagent/proposed-agents.yaml`; render a **Proposed** section atop the roster.
- Spawn a proposed agent (or all) via the existing add-agent path.

**Out of scope (later slices)**
- The **Styloagent MCP server** + `spawn_agent`, so the overview *starts* children itself and
  subsystems split recursively (Theme 4).
- Deep git-history analysis, saved-context/restart-prompt generation per proposed agent, per-agent
  git worktrees.

---

## 3. Project model + scaffolding

A **project** is any directory. Its Styloagent state lives in a `.styloagent/` subfolder:

```
<project>/.styloagent/
  system-prompt.md        # the overview agent's role + how to propose the team
  PROTOCOL.md             # coordination protocol
  proposed-agents.yaml    # written by the overview; read by Styloagent
  channel/                # the file-drop bus (this is the channelRoot)
    inbox/  outbox/  archive/inbox/  archive/outbox/
```

**`ProjectConfig` (Core, record):**
```csharp
public sealed record ProjectConfig(
    string Root,               // the project directory
    string ConfigDir,          // <Root>/.styloagent
    string SystemPromptPath,   // <ConfigDir>/system-prompt.md
    string ProtocolPath,       // <ConfigDir>/PROTOCOL.md
    string ChannelRoot,        // <ConfigDir>/channel
    string ProposedAgentsPath);// <ConfigDir>/proposed-agents.yaml

public static ProjectConfig For(string root); // builds the paths (no I/O)
```

**`ProjectScaffolder` (Core):**
```csharp
public static class ProjectScaffolder
{
    // Ensures the .styloagent tree + channel dirs exist and writes the DEFAULT system prompt and
    // PROTOCOL only when absent (never overwrites the project's own). Idempotent. Returns the config.
    public static ProjectConfig Ensure(string root);
}
```
The bundled defaults are string constants in Core (`DefaultTemplates.SystemPrompt`,
`DefaultTemplates.Protocol`). The default **system prompt** instructs the overview to: analyse the
repo's shape, decide the initial 3-4 subsystems, and write them to `proposed-agents.yaml` in the
exact schema below (this schema is the contract between the scaffolded prompt and `ProposedAgentsReader`).

`proposed-agents.yaml` schema:
```yaml
agents:
  - prefix: foss-
    responsibility: owns the FOSS packages
    dir: .                     # relative to project root (worktree/subdir)
    launchPrompt: |
      You are the `foss-` agent. You own the FOSS packages. …
```

---

## 4. Welcome screen + recents

**`RecentProjectsStore` (App, VYaml, mirrors `PresentationStore`):** persists a capped, de-duplicated,
most-recent-first list of project paths in the app config dir. `Load()` / `Add(path)`.

**`IFolderPicker` (App abstraction, for testability):**
```csharp
public interface IFolderPicker { Task<string?> PickFolderAsync(); }
```
Real impl wraps `TopLevel.StorageProvider.OpenFolderPickerAsync`; tests use a fake returning a
fixed path.

**`WelcomeViewModel` (App):** `ObservableCollection<string> Recent` (from the store);
`OpenFolderCommand` (invokes `IFolderPicker`); `OpenRecentCommand(string path)`. Both raise an
`Action<string> onProjectChosen` the shell handles. `WelcomeView`: title, a big **Open a project
folder…** button, and the recents list.

---

## 5. Launching the overview agent

On project open, after `ProjectScaffolder.Ensure`, the shell builds the cockpit against the project
(`channelRoot = config.ChannelRoot`, `repoRoot = config.Root`) and launches the **overview** agent
as the first pane:

- prefix `overview-`, working directory = `config.Root`.
- Launch args include the system prompt: `["--append-system-prompt", <contents of system-prompt.md>]`
  (threaded through the existing `AgentSession` `launchArgs`). **Plan-time verification:** confirm
  Claude Code accepts `--append-system-prompt <string>`; if not, prepend the system prompt to the
  launch prompt (first message) instead.
- Launch prompt (first message): a short task referencing the PROTOCOL and asking it to analyse the
  repo and write `proposed-agents.yaml`.

---

## 6. Proposed team → spawn

**`ProposedAgent` (Core, record):** `(string Prefix, string Responsibility, string Dir, string LaunchPrompt)`.

**`ProposedAgentsReader` (Core):** `Read(string path) : IReadOnlyList<ProposedAgent>` — VYaml parse;
tolerant (returns empty on missing/invalid; never throws).

**`ProposedTeamViewModel` (App):** watches `ProposedAgentsPath` (FileSystemWatcher + debounce, same
pattern as `BusViewModel`) → `ObservableCollection<ProposedAgentItem>` (Prefix, Responsibility,
`SpawnCommand`), plus `SpawnAllCommand`. Spawning invokes an injected
`Action<ProposedAgent> spawn` handled by `MainWindowViewModel`.

**Roster "Proposed" section:** `AgentsView` gains a **PROPOSED** group at the top (above the live
`Panes`), each row a card (colour dot by `DefaultColorFor(prefix)` · prefix · responsibility · a
`Spawn` button), plus a `Spawn all` header action. It binds `MainWindowViewModel.ProposedTeam`.

**Spawn:** `MainWindowViewModel.SpawnProposed(ProposedAgent p)` mirrors `AddAgent` — builds an
`AgentManifestEntry` from `p` (prefix, worktree = `Path.Combine(Root, p.Dir)` resolved, launch
prompt = `p.LaunchPrompt`), reserves a hook id, creates the pane, adds it to the roster + the
DocumentDock, spawns it, and removes `p` from the proposals. `Spawn all` iterates.

---

## 7. Startup wiring

`App.OnFrameworkInitializationCompleted`:
- If `STYLOAGENT_REPO` is set (CLI shortcut), open that project directly (scaffold + cockpit).
- Otherwise show the **Welcome** window. On project chosen → `ProjectScaffolder.Ensure` →
  `RecentProjectsStore.Add` → build `MainWindowViewModel.InitializeAsync(config.ChannelRoot, …,
  repoRoot: config.Root, systemPromptPath: config.SystemPromptPath)` (new optional param to launch
  the overview with its system prompt) → swap the window content to the cockpit.

`MainWindowViewModel.InitializeAsync` gains an optional `string? overviewSystemPromptPath`. When
set, the bootstrap path launches **exactly one** agent — the `overview-` agent (working dir =
project root, system prompt injected) — and **bypasses the worktree auto-seeding** entirely: the
team is proposed by the overview, not derived from git worktrees. When unset, behaviour is
unchanged (worktree/channel seeding as today).

---

## 8. Data flow

```
startup → Welcome (recents) → pick folder
  → ProjectScaffolder.Ensure(folder) → ProjectConfig
  → RecentProjectsStore.Add(folder)
  → MainWindowViewModel.InitializeAsync(channelRoot=config.ChannelRoot, repoRoot=config.Root,
                                        overviewSystemPromptPath=config.SystemPromptPath)
       → launch overview- (cd config.Root, --append-system-prompt, task launch prompt)
  → overview writes .styloagent/proposed-agents.yaml
  → ProposedTeamViewModel (watch) → PROPOSED cards in the roster
  → human clicks Spawn → SpawnProposed → live roster agent (proposal removed)
```

---

## 9. Components / files

**Core (create):** `Projects/ProjectConfig.cs`, `Projects/ProjectScaffolder.cs`,
`Projects/DefaultTemplates.cs`, `Projects/ProposedAgent.cs`, `Projects/ProposedAgentsReader.cs`.
**App (create):** `Config/RecentProjectsStore.cs`, `Services/IFolderPicker.cs` (+ Avalonia impl),
`ViewModels/WelcomeViewModel.cs`, `ViewModels/ProposedTeamViewModel.cs`,
`Views/WelcomeView.axaml(.cs)`.
**App (modify):** `App.axaml.cs` (welcome-first startup), `ViewModels/MainWindowViewModel.cs`
(overview launch + `SpawnProposed` + expose `ProposedTeam`), `Views/AgentsView.axaml` (PROPOSED
section), `App.axaml` (Welcome/proposed data templates as needed).

---

## 10. Testing

- **Core:** `ProjectScaffolder` creates the tree + defaults and is idempotent (second call doesn't
  overwrite an edited system prompt); `ProposedAgentsReader` parses the schema, tolerates
  missing/invalid; `ProjectConfig.For` builds the right paths.
- **App VM:** `RecentProjectsStore` add/dedupe/cap + round-trip; `WelcomeViewModel.OpenFolder`
  (fake `IFolderPicker`) raises `onProjectChosen`; `ProposedTeamViewModel` picks up a written yaml
  file → cards, and `SpawnCommand` invokes the spawn callback; `MainWindowViewModel.SpawnProposed`
  adds a pane and removes the proposal.
- **UITests:** `WelcomeView` renders (title + Open button + a recent) and the roster **PROPOSED**
  section renders cards with Spawn buttons (ItemsControl materialises with the theme); screenshots
  for the README.

---

## 11. Resolved decisions

- Entry: **Welcome picker + scaffold-if-missing** (recents persisted; `STYLOAGENT_REPO` still opens
  directly).
- Overview: **suggests** the team (no MCP); the human spawns.
- Proposal channel: **structured `.styloagent/proposed-agents.yaml`** watched by Styloagent.
- Proposed-team UI: a **PROPOSED section at the top of the Agents roster**.
- System-prompt injection: **`--append-system-prompt`** from `system-prompt.md` (fallback: prepend
  to the launch prompt; verified in the plan).
