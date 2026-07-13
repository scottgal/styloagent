# Worktree Survives Spawn — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry the architect's per-agent `worktree` decision from `proposed-agents.yaml` through the human **Spawn** action by routing it onto the one governed spawn path.

**Architecture:** Add a `worktree` field to the proposal schema (Core). Route `MainWindowViewModel.SpawnProposed` through the governed `SpawnChild` (which already creates worktrees), with a documented direct-create exception for the root/no-owner case. Surface governor rejections on the roster card (the price of putting the human click behind the governor). Teach the field in the architect's prompt.

**Tech Stack:** .NET 10 · Avalonia 11.3 · CommunityToolkit.Mvvm · VYaml · xUnit.

## Global Constraints

- Target **.NET 10 / Avalonia 11.3**; no new NuGet dependencies.
- VYaml `[YamlObject]` default naming is **LowerCamelCase** → a `Worktree` property maps to the YAML key `worktree`.
- **Backward compatible:** a missing `worktree` key parses to `false`. Every existing `proposed-agents.yaml` / `team.yaml` must still load unchanged.
- **Single-rooted authority invariant** (`lint_authority`, commit `a6e8a52`) must hold — the roster-spawn must not create a second root.
- **Degrade, never destroy:** a failed worktree add returns a `SpawnOutcome.Reject`, never throws; durable files untouched.
- Run tests with: `dotnet test --filter "<expr>"` from the repo root.

---

### Task 1: Add `worktree` to the proposal schema (Core)

**Files:**
- Modify: `src/Styloagent.Core/Projects/ProposedAgent.cs:4`
- Modify: `src/Styloagent.Core/Projects/ProposedAgentsReader.cs:13-18,35-36`
- Test: `tests/Styloagent.Core.Tests/ProposedAgentsReaderTests.cs`

**Interfaces:**
- Produces: `ProposedAgent(string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree = false)` — the 5th member is defaulted, so all existing 4-arg positional constructions keep compiling.

- [ ] **Step 1: Write the failing test** — append to `ProposedAgentsReaderTests`:

```csharp
    [Fact]
    public void Read_parses_the_worktree_flag_and_defaults_it_false()
    {
        var path = Path.Combine(Path.GetTempPath(), "pa-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n" +
            "  - prefix: iso-\n" +
            "    responsibility: overlaps foss\n" +
            "    dir: .\n" +
            "    worktree: true\n" +
            "    launchPrompt: You are iso-.\n" +
            "  - prefix: share-\n" +
            "    responsibility: shares the repo\n" +
            "    dir: .\n" +
            "    launchPrompt: You are share-.\n");   // no worktree key → defaults false
        try
        {
            var agents = ProposedAgentsReader.Read(path);
            Assert.Equal(2, agents.Count);
            Assert.True(agents[0].Worktree);
            Assert.False(agents[1].Worktree);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ProposedAgentsReaderTests.Read_parses_the_worktree_flag"`
Expected: FAIL to compile — `ProposedAgent` has no `Worktree` member.

- [ ] **Step 3: Add the field to the record** — `ProposedAgent.cs:4`:

```csharp
public sealed record ProposedAgent(string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree = false);
```

- [ ] **Step 4: Add the YAML row + thread it through the reader** — `ProposedAgentsReader.cs`.

In `ProposedAgentRow` (after `LaunchPrompt`):

```csharp
    public bool Worktree { get; set; }
```

In `Read`, change the `list.Add(...)` to pass the flag:

```csharp
                list.Add(new ProposedAgent(r.Prefix.Trim(), r.Responsibility.Trim(),
                    string.IsNullOrWhiteSpace(r.Dir) ? "." : r.Dir.Trim(), r.LaunchPrompt, r.Worktree));
```

- [ ] **Step 5: Run the new test + the existing reader tests to verify all pass**

Run: `dotnet test --filter "FullyQualifiedName~ProposedAgentsReaderTests"`
Expected: PASS (3 tests — the two existing + the new one; the existing ones prove backward-compat).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Core/Projects/ProposedAgent.cs src/Styloagent.Core/Projects/ProposedAgentsReader.cs tests/Styloagent.Core.Tests/ProposedAgentsReaderTests.cs
git commit -m "feat(fleet): proposed-agents schema carries the worktree decision"
```

---

### Task 2: Route `SpawnProposed` through the governed path, honouring worktree (App)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — extract `TryAddWorktree`; refactor `SpawnChild` (`:1117-1128`); rewrite `SpawnProposed` (`:1096-1103`).
- Test: `tests/Styloagent.App.Tests/FleetSpawnTests.cs`

**Interfaces:**
- Consumes: `ProposedAgent.Worktree` (Task 1); `SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree)`; `SpawnOutcome.Ok(prefix)` / `SpawnOutcome.Reject(reason, msg)`; `WorktreeNaming.For`; `IGitService.AddWorktreeAsync`.
- Produces: `public SpawnOutcome SpawnProposed(ProposedAgent p)` — now returns an outcome; `private bool TryAddWorktree(string prefix, out string? path, out string? branch, out string? error)`.

- [ ] **Step 1: Write the failing test** — append to `FleetSpawnTests` (reuses the existing `RecordingGitService` in this file):

```csharp
    [Fact]
    public async Task SpawnProposed_with_worktree_true_creates_an_agent_branch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "psp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new RecordingGitService();
        try
        {
            var cfg = ProjectScaffolder.Ensure(repo);
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                gitService: git, repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);
            Assert.Equal("overview-", vm.Panes[0].Prefix);

            var outcome = vm.SpawnProposed(
                new ProposedAgent("iso-", "overlaps foss", ".", "You are iso-.", Worktree: true));

            Assert.True(outcome.Spawned);
            Assert.Equal("agent/iso", git.AddedBranch);
            var pane = vm.Panes.First(p => p.Prefix == "iso-");
            Assert.Equal("agent/iso", pane.WorktreeBranch);
            Assert.Equal("overview-", pane.ParentPrefix);   // still owned by the overview
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FleetSpawnTests.SpawnProposed_with_worktree_true"`
Expected: FAIL — `ProposedAgent` has no `Worktree`-aware path in `SpawnProposed` (no branch added; `git.AddedBranch` is null).

- [ ] **Step 3: Extract the worktree-add helper.** In `MainWindowViewModel`, add this private method (place it just above `SpawnChild`):

```csharp
    /// <summary>
    /// Creates an isolated <c>agent/&lt;prefix&gt;</c> worktree for a spawning agent when a git service and
    /// project are present. Returns true and sets <paramref name="path"/>/<paramref name="branch"/> on
    /// success; false with <paramref name="error"/> if the worktree add fails. A no-op that returns true
    /// with null path/branch when there is no git service or project — the agent then shares the repo.
    /// </summary>
    private bool TryAddWorktree(string prefix, out string? path, out string? branch, out string? error)
    {
        path = null; branch = null; error = null;
        if (_git is null || _project is null) return true;   // nothing to isolate; share the repo
        var existing = Panes.Where(p => p.WorktreePath is not null).Select(p => p.WorktreePath!);
        var (wtPath, wtBranch) = WorktreeNaming.For(_project.Root, prefix, existing);
        var add = _git.AddWorktreeAsync(_project.Root, wtPath, wtBranch).GetAwaiter().GetResult();
        if (!add.Ok) { error = add.Error; return false; }
        EnsureWorktreesIgnored(_project.Root);
        path = wtPath; branch = wtBranch;
        return true;
    }
```

- [ ] **Step 4: Refactor `SpawnChild` to use the helper.** Replace the block at `:1117-1128` with:

```csharp
        string? worktreePath = null, worktreeBranch = null;
        if (req.Worktree && !TryAddWorktree(req.Prefix, out worktreePath, out worktreeBranch, out var wtError))
            return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {wtError}");
```

(The rest of `SpawnChild` — `CreatePaneForProposed(..., worktreeOverride: worktreePath, worktreeBranch: worktreeBranch)` and the `RefreshGitStatusAsync` call — is unchanged.)

- [ ] **Step 5: Rewrite `SpawnProposed`** (`:1096-1103`) to return an outcome and route through the governed path:

```csharp
    /// <summary>
    /// Turns a proposed subsystem into a live roster agent. Normal case: routes through the governed
    /// <see cref="SpawnChild"/>, so a human-spawn gets the same governor + worktree + lineage as an
    /// agent's <c>spawn_agent</c>. Sole exception: with no overview owner (a bare worktree roster) this
    /// is a ROOT spawn — the parent-centric governor can't check a root without risking a second root
    /// (breaking single-rooted authority), so it creates the pane directly, still honouring the
    /// proposal's worktree decision.
    /// </summary>
    public SpawnOutcome SpawnProposed(ProposedAgent p)
    {
        var owner = OverviewPane();
        if (owner is not null && owner.Prefix != p.Prefix)
            return SpawnChild(new SpawnRequest(
                owner.Prefix, p.Prefix, p.Responsibility, p.Dir, p.LaunchPrompt, p.Worktree));

        // Root / no-owner exception: establish the single root directly.
        string? worktreePath = null, worktreeBranch = null;
        if (p.Worktree && !TryAddWorktree(p.Prefix, out worktreePath, out worktreeBranch, out var wtError))
            return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {wtError}");
        var pane = CreatePaneForProposed(p, worktreeOverride: worktreePath, worktreeBranch: worktreeBranch);
        if (worktreePath is not null && _git is not null && pane is not null)
            _ = pane.RefreshGitStatusAsync(_git);
        return pane is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(p.Prefix);
    }
```

- [ ] **Step 6: Run the new test + the existing spawn tests to verify all pass**

Run: `dotnet test --filter "FullyQualifiedName~FleetSpawnTests"`
Expected: PASS. In particular `SpawnProposed_is_owned_by_the_overview_keeping_the_authority_tree_single_rooted` still passes (owner exists → routes through `SpawnChild` with `Worktree:false`; pane is parented to `overview-`, depth+1, single-rooted, dehydratable) and `Spawn_with_worktree_creates_an_agent_branch` still passes (the `SpawnChild` refactor is behaviour-preserving).

- [ ] **Step 7: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/FleetSpawnTests.cs
git commit -m "feat(fleet): human roster-spawn goes through the governed worktree path"
```

---

### Task 3: Surface governor rejections on the roster card (App)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/ProposedTeamViewModel.cs` — `ProposedAgentItem` → observable `RejectionMessage`; callback type; `Spawn`/`SpawnAll`.
- Modify: `src/Styloagent.App/Views/AgentsView.axaml` — add the rejection TextBlock (after `:111`).
- Test: `tests/Styloagent.App.Tests/ProposedTeamViewModelTests.cs`

**Interfaces:**
- Consumes: `SpawnOutcome` (`Styloagent.Core.Mcp`) — `.Spawned`, `.Message`.
- Produces: callback `Func<ProposedAgent, SpawnOutcome> spawn`; `ProposedAgentItem.RejectionMessage` (observable `string?`).

- [ ] **Step 1: Write the failing tests.** First migrate the three existing callbacks in this file to return an outcome (they currently return void/assignment and will not compile against the new `Func` signature):
  - `a => spawned = a` → `a => { spawned = a; return SpawnOutcome.Ok(a.Prefix); }`
  - `a => spawned.Add(a)` → `a => { spawned.Add(a); return SpawnOutcome.Ok(a.Prefix); }`
  - `_ => { }` → `_ => SpawnOutcome.Ok("x-")`

  Add `using Styloagent.Core.Mcp;` to the file, then append:

```csharp
    [Fact]
    public void Spawn_removes_card_on_success()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n  - prefix: foss-\n    responsibility: packages\n    dir: .\n    launchPrompt: hi\n");
        try
        {
            var vm = new ProposedTeamViewModel(path, null, a => SpawnOutcome.Ok(a.Prefix));
            vm.Refresh();
            vm.SpawnCommand.Execute(vm.Proposals[0].Agent);
            Assert.Empty(vm.Proposals);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Spawn_keeps_card_and_shows_message_when_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n  - prefix: foss-\n    responsibility: packages\n    dir: .\n    launchPrompt: hi\n");
        try
        {
            var vm = new ProposedTeamViewModel(path, null,
                _ => SpawnOutcome.Reject(RejectReason.FleetFull, "fleet full (12/12)"));
            vm.Refresh();
            vm.SpawnCommand.Execute(vm.Proposals[0].Agent);
            Assert.Single(vm.Proposals);                                   // card stays
            Assert.Equal("fleet full (12/12)", vm.Proposals[0].RejectionMessage);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ProposedTeamViewModelTests"`
Expected: FAIL to compile — the ctor still takes `Action<ProposedAgent>`; `RejectionMessage` does not exist.

- [ ] **Step 3: Make `ProposedAgentItem` observable.** In `ProposedTeamViewModel.cs`, replace the `ProposedAgentItem` class with:

```csharp
/// <summary>One proposed subsystem card in the roster's PROPOSED section.</summary>
public sealed partial class ProposedAgentItem : ObservableObject
{
    public ProposedAgent Agent { get; init; } = null!;
    public string Prefix { get; init; } = "";
    public string Responsibility { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";

    /// <summary>Set when a Spawn is rejected by the governor; shown in red on the card.</summary>
    [ObservableProperty]
    private string? _rejectionMessage;
}
```

(`using CommunityToolkit.Mvvm.ComponentModel;` is already imported.)

- [ ] **Step 4: Change the callback type + Spawn/SpawnAll behaviour.**

Add `using Styloagent.Core.Mcp;` to the top of `ProposedTeamViewModel.cs`. Change the field and ctor param `Action<ProposedAgent>` → `Func<ProposedAgent, SpawnOutcome>` (field `_spawn`, ctor parameter `spawn`). Replace `Spawn`:

```csharp
    [RelayCommand]
    private void Spawn(ProposedAgent agent)
    {
        var outcome = _spawn(agent);
        var item = Proposals.FirstOrDefault(p => ReferenceEquals(p.Agent, agent));
        if (item is null) return;
        if (outcome.Spawned) Proposals.Remove(item);
        else item.RejectionMessage = outcome.Message;
    }
```

`SpawnAll` is unchanged in body (it calls `Spawn` per card); rejected cards now simply remain with their message instead of being cleared.

- [ ] **Step 5: Add the rejection line to the card** — `AgentsView.axaml`, immediately after the Responsibility `TextBlock` (`:111`), inside the same `StackPanel`:

```xml
                        <TextBlock Text="{Binding RejectionMessage}" FontSize="10" Foreground="#E5736B"
                                   TextWrapping="Wrap"
                                   IsVisible="{Binding RejectionMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
```

- [ ] **Step 6: Wire the callback.** No change needed at `MainWindowViewModel.cs:872` — `SpawnProposed` now returns `SpawnOutcome`, so the method-group binds to `Func<ProposedAgent, SpawnOutcome>` directly. Confirm it still compiles.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ProposedTeamViewModelTests"`
Expected: PASS (the three migrated tests + the two new ones).

- [ ] **Step 8: Commit**

```bash
git add src/Styloagent.App/ViewModels/ProposedTeamViewModel.cs src/Styloagent.App/Views/AgentsView.axaml tests/Styloagent.App.Tests/ProposedTeamViewModelTests.cs
git commit -m "feat(fleet): show governor rejection on the PROPOSED roster card"
```

---

### Task 4: Teach the architect the `worktree` field (docs)

**Files:**
- Modify: `src/Styloagent.Core/Projects/DefaultTemplates.cs:54-60` (the schema block in `SystemPrompt`).
- Modify: `.styloagent/system-prompt.md` (the live copy — same schema block).
- Test: `tests/Styloagent.Core.Tests/DefaultTemplatesTests.cs` (new).

**Interfaces:**
- Consumes: nothing. Produces: nothing runtime — this closes the contract/capability gap so the architect actually emits `worktree:`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/DefaultTemplatesTests.cs`:

```csharp
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class DefaultTemplatesTests
{
    [Fact]
    public void SystemPrompt_proposal_schema_teaches_the_worktree_field()
        => Assert.Contains("worktree:", DefaultTemplates.SystemPrompt);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~DefaultTemplatesTests"`
Expected: FAIL — the schema block doesn't yet mention `worktree:`.

- [ ] **Step 3: Add the field to the schema block** in `DefaultTemplates.cs` (between `dir:` and `launchPrompt:`):

```
    agents:
      - prefix: foss-
        responsibility: owns the FOSS packages
        dir: .
        worktree: false   # true only when this agent's work overlaps files another agent owns
        launchPrompt: |
          You are the `foss-` agent. You own the FOSS packages. Coordinate with the fleet via the
          `send_message` MCP tool — read `.styloagent/PROTOCOL.md` first.
```

- [ ] **Step 4: Mirror the change in the live prompt** — make the identical edit to the `agents:` schema block in `.styloagent/system-prompt.md` (add the `worktree: false` line with the same comment). *(The two files duplicate the architect contract; both must carry the field — see the design doc §5.)*

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~DefaultTemplatesTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Core/Projects/DefaultTemplates.cs .styloagent/system-prompt.md tests/Styloagent.Core.Tests/DefaultTemplatesTests.cs
git commit -m "docs(prompt): teach the proposed-agents worktree field to the architect"
```

---

### Task 5: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Build the solution**

Run: `dotnet build Styloagent.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the whole test suite**

Run: `dotnet test`
Expected: All tests pass (new: worktree parse, `SpawnProposed` worktree branch, roster rejection card, schema teaches worktree; all pre-existing tests green — proving backward-compat and the behaviour-preserving `SpawnChild` refactor).

- [ ] **Step 3: Hand off**

If this branch was spawned with a worktree, call `wrap_up()` (Styloagent runs the tests, merges to main, removes the worktree). Otherwise open a PR from `fix/worktree-survives-spawn`.

---

## Self-Review

- **Spec coverage:** §2.1 schema → Task 1 + Task 4; §2.2 unified path + root exception → Task 2; §2.3 human-click rejection UX → Task 3; §4 testing → the tests in Tasks 1–3 + Task 5. All spec sections map to a task.
- **Placeholder scan:** no TBD/TODO; every code step shows full code; every run step shows the command + expected result.
- **Type consistency:** `SpawnProposed` returns `SpawnOutcome` (Tasks 2, 3, wiring); `TryAddWorktree(string, out string?, out string?, out string?)` defined in Task 2 and used in both branches; `Func<ProposedAgent, SpawnOutcome>` callback (Task 3) matches `SpawnProposed`'s new signature; `ProposedAgent(…, bool Worktree = false)` (Task 1) matches the 5-arg construction in Task 2's test and the existing 4-arg calls.
- **Scope:** one branch, one subsystem (fleet spawn), five ordered tasks each independently testable.
