# Git Errors + Branch + Stash (Plan 2e) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the git client's write ops — surface git failures to the human (push/pull failures are currently silent), and add branch (list/current/create/switch) and stash (save/pop/list) to the Changes panel.

**Architecture:** `ChangesViewModel` gains a `WriteError` surfaced in a Changes-panel banner (set from any failed write `GitResult`, cleared on success/new op). Two new seams on `Styloagent.Git` mirror `IGitWrite`: `IGitBranch` (list/current/create/switch) and `IGitStash` (save/pop/list), implemented by the shared `GitService`. The panel gains a branch bar (current branch + switch/create) and a stash control. Human panel actions only — no MCP tools.

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, xUnit. Git CLI ≥ 2.25.1 (`git switch`).

## Global Constraints

- net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build clean (0 warnings/0 errors; pre-existing NU1903 not ours).
- Git access never throws across the seam — ops return `GitResult`/`GitResult<T>` carrying stderr; the VM surfaces the stderr in `WriteError` on failure.
- No destructive ops beyond what's requested (stash/branch are non-destructive; no `reset --hard`, `clean -fd`, `push --force`).
- `GitService` write/query methods reuse the private `RunAsync` (already `.ConfigureAwait(false)`); VM methods are awaited (not blocked-on), set UI-bound state → no `.ConfigureAwait(false)`.
- `ArgumentList` (no shell): branch names, stash messages passed as single argv elements.
- Human panel actions — NO new MCP tools.
- Commit directly to `main` (no new branch), authored `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` + trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Create:**
- `src/Styloagent.Git/IGitBranch.cs` — branch seam.
- `src/Styloagent.Git/IGitStash.cs` — stash seam.
- `src/Styloagent.Core/Git/GitBranch.cs` — `GitBranch` record (UI-free).
- Tests: extend `GitServiceIntegrationTests`, `ChangesViewModelTests`; add render coverage in `ChangesWriteViewTests`.

**Modify:**
- `src/Styloagent.Git/GitService.cs` — implement `IGitBranch` + `IGitStash`.
- `src/Styloagent.App/ViewModels/ChangesViewModel.cs` — `WriteError`; branch/stash state + commands; surface errors from every write op.
- `src/Styloagent.App/Views/ChangesView.axaml` — error banner + branch bar + stash control.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` / `App.axaml.cs` — pass the shared `GitService` as `IGitBranch`/`IGitStash` (same instance).

---

## Task 1: Surface write-op errors

**Files:**
- Modify: `src/Styloagent.App/ViewModels/ChangesViewModel.cs`, `src/Styloagent.App/Views/ChangesView.axaml`
- Test: `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`

**Interfaces:**
- Produces: `ChangesViewModel.WriteError` (`[ObservableProperty] string? _writeError`) + `bool HasWriteError => !string.IsNullOrEmpty(WriteError)`; a private helper `void Report(GitResult r)` that sets `WriteError = r.Ok ? null : r.Error;` — called by every write op. New ops / successful ops clear it.

- [ ] **Step 1: Write the failing test** — extend `ChangesViewModelTests.cs`. Make `FakeWrite` able to return a failure: add `public bool NextFails;` and have each method return `NextFails ? GitResult.Fail("boom") : GitResult.Success()`. Test: with `NextFails=true`, `await PushAsync()` sets `WriteError == "boom"` and `HasWriteError` true; then a successful op clears it.

```csharp
    [Fact]
    public async Task Failed_write_op_surfaces_the_error_then_clears_on_success()
    {
        var write = new FakeWrite { NextFails = true };
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), write);
        await vm.LoadAsync("/wt");

        await vm.PushAsync();
        Assert.True(vm.HasWriteError);
        Assert.Equal("boom", vm.WriteError);

        write.NextFails = false;
        await vm.PullAsync();
        Assert.False(vm.HasWriteError);
        Assert.Null(vm.WriteError);
    }
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.App.Tests/... --filter "FullyQualifiedName~ChangesViewModelTests"` → FAIL.

- [ ] **Step 3: Implement** — in `ChangesViewModel`:
```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWriteError))]
    private string? _writeError;

    public bool HasWriteError => !string.IsNullOrEmpty(WriteError);

    private void Report(GitResult r) => WriteError = r.Ok ? null : r.Error;
```
Update each write method to capture + report the result, e.g.:
```csharp
    public async Task StageAsync(GitChange c) { Report(await _write.StageAsync(_worktreePath, c.Path)); await LoadAsync(_worktreePath); }
    public async Task UnstageAsync(GitChange c) { Report(await _write.UnstageAsync(_worktreePath, c.Path)); await LoadAsync(_worktreePath); }
    public async Task CommitAsync() { if (!CanCommit) return; var r = await _write.CommitAsync(_worktreePath, CommitMessage); Report(r); if (r.Ok) CommitMessage = ""; await LoadAsync(_worktreePath); }
    public async Task PushAsync() { Report(await _write.PushAsync(_worktreePath)); await LoadAsync(_worktreePath); }
    public async Task PullAsync() { Report(await _write.PullAsync(_worktreePath)); await LoadAsync(_worktreePath); }
```
`Clear()` also sets `WriteError = null`.
Add an error banner to `ChangesView.axaml` (visible when `HasWriteError`), e.g. a `Border` with `IsVisible="{Binding HasWriteError}"`, an attention background (`{DynamicResource AttentionBrush}` or a theme error brush), and a `TextBlock Text="{Binding WriteError}"` (wrapped, small). Place it above the commit bar.

- [ ] **Step 4: Run + commit** — focused test → PASS; full App.Tests → green.

```bash
git add src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/Views/ChangesView.axaml tests/Styloagent.App.Tests/ChangesViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): surface write-op failures in the Changes panel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Branch seam — list / current / create / switch

**Files:**
- Create: `src/Styloagent.Core/Git/GitBranch.cs`, `src/Styloagent.Git/IGitBranch.cs`
- Modify: `src/Styloagent.Git/GitService.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `record GitBranch(string Name, bool IsCurrent)` (Core, UI-free).
  - `interface IGitBranch` (Styloagent.Git): `Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string worktreePath, CancellationToken ct = default)`, `Task<GitResult> CreateBranchAsync(string worktreePath, string name, CancellationToken ct = default)`, `Task<GitResult> SwitchBranchAsync(string worktreePath, string name, CancellationToken ct = default)`.
  - `GitService : …, IGitBranch`.

- [ ] **Step 1: Create the model + seam** — `GitBranch.cs` (record above); `IGitBranch.cs` (interface above).

- [ ] **Step 2: Write the failing integration test** — add to `GitServiceIntegrationTests.cs` (reuses helpers, skips without git):

```csharp
    [Fact]
    public async Task Branch_create_switch_and_list()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitbranch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.CreateBranchAsync(repo, "feature")).Ok);        // creates + switches
            var list = await git.ListBranchesAsync(repo);
            Assert.True(list.Ok);
            Assert.Contains(list.Value!, b => b.Name == "feature" && b.IsCurrent);
            Assert.Contains(list.Value!, b => b.Name == "main" && !b.IsCurrent);
            Assert.True((await git.SwitchBranchAsync(repo, "main")).Ok);
            var after = await git.ListBranchesAsync(repo);
            Assert.Contains(after.Value!, b => b.Name == "main" && b.IsCurrent);
        }
        finally { TryDeleteRepo(repo); }
    }
```

- [ ] **Step 3: Run to verify it fails** — FAIL.

- [ ] **Step 4: Implement** in `GitService`:
```csharp
    public async Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string worktreePath, CancellationToken ct = default)
    {
        var current = await RunAsync(worktreePath, ct, "branch", "--show-current").ConfigureAwait(false);
        if (!current.Ok) return GitResult<IReadOnlyList<GitBranch>>.Fail(current.Stderr);
        var currentName = current.Stdout.Trim();

        var r = await RunAsync(worktreePath, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads").ConfigureAwait(false);
        if (!r.Ok) return GitResult<IReadOnlyList<GitBranch>>.Fail(r.Stderr);

        var branches = new List<GitBranch>();
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            branches.Add(new GitBranch(name, name == currentName));
        }
        return GitResult<IReadOnlyList<GitBranch>>.Success(branches);
    }

    public async Task<GitResult> CreateBranchAsync(string worktreePath, string name, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "switch", "-c", name).ConfigureAwait(false));

    public async Task<GitResult> SwitchBranchAsync(string worktreePath, string name, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "switch", name).ConfigureAwait(false));
```
Add `IGitBranch` to the class base list; add `using Styloagent.Core.Git;` if needed.

- [ ] **Step 5: Run + commit** — test → PASS (or skips); `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Core/Git/GitBranch.cs src/Styloagent.Git/IGitBranch.cs src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): IGitBranch list/current/create/switch

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Branch UI + VM

**Files:**
- Modify: `src/Styloagent.App/ViewModels/ChangesViewModel.cs`, `src/Styloagent.App/Views/ChangesView.axaml`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`, `src/Styloagent.App/App.axaml.cs`
- Test: `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitBranch` (Task 2).
- Produces on `ChangesViewModel`: ctor gains `IGitBranch branch`; `ObservableCollection<GitBranch> Branches`; `[ObservableProperty] string? _currentBranch`; `[RelayCommand] Task Switch(GitBranch b)` (switch + reload branches + `LoadAsync`); `[RelayCommand] Task CreateBranch()` using `[ObservableProperty] string _newBranchName` (create + reload + clear name); a `LoadBranchesAsync()` called from `LoadAsync`.

- [ ] **Step 1: Write the failing test** — add a `FakeBranch : IGitBranch` (returns two branches, records create/switch names). Assert: after `LoadAsync`, `Branches.Count==2` and `CurrentBranch` set; `await SwitchAsync(other)` calls `branch.SwitchBranchAsync`; `CreateBranch` with `NewBranchName` calls `branch.CreateBranchAsync` and clears the name. Update the ctor calls in existing tests to the new arity.

- [ ] **Step 2: Run to verify it fails** — FAIL.

- [ ] **Step 3: Implement** — extend `ChangesViewModel` (branch collection + current + commands, each `Report(...)`ing failures per Task 1 and reloading). In `LoadAsync`, also call `await LoadBranchesAsync()` (fetch `ListBranchesAsync`, refill `Branches`, set `CurrentBranch` from the `IsCurrent` one). Wire the new `IGitBranch` through `MainWindowViewModel` (`Changes = new ChangesViewModel(git, diff, write, branch)`) + the shared-instance cast in `App.axaml.cs`/`MainWindowViewModel` (GitService implements `IGitBranch`).
- `ChangesView.axaml`: add a **branch bar** at the top — current branch (`ComboBox` bound to `Branches`, `SelectedItem` → a switch, showing `Name`; mark the current) OR a label + a small dropdown; plus a create affordance (a `TextBox` bound `NewBranchName` + a **New branch** button → `CreateBranchCommand`). Keep it compact; theme tokens.

- [ ] **Step 4: Run + commit** — focused test → PASS; full App.Tests → green; build clean.

```bash
git add src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/Views/ChangesView.axaml src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/App.axaml.cs tests/Styloagent.App.Tests/ChangesViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): branch bar — current branch + switch/create in the Changes panel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Stash seam — save / pop / list

**Files:**
- Create: `src/Styloagent.Git/IGitStash.cs`
- Modify: `src/Styloagent.Git/GitService.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`

**Interfaces:**
- Produces: `interface IGitStash` (Styloagent.Git): `Task<GitResult> StashAsync(string worktreePath, string? message, CancellationToken ct = default)`, `Task<GitResult> StashPopAsync(string worktreePath, CancellationToken ct = default)`, `Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string worktreePath, CancellationToken ct = default)`. `GitService : …, IGitStash`.

- [ ] **Step 1: Create the seam** — `IGitStash.cs`.

- [ ] **Step 2: Write the failing integration test** — a stash round-trip (skips without git): commit a file, modify it, `StashAsync` → status clean, `ListStashesAsync` returns 1 entry, `StashPopAsync` → the modification is back (status dirty).

```csharp
    [Fact]
    public async Task Stash_save_list_and_pop()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitstash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.StashAsync(repo, "wip")).Ok);
            Assert.False((await git.GetStatusAsync(repo)).Value!.IsDirty);
            var list = await git.ListStashesAsync(repo);
            Assert.True(list.Ok); Assert.Single(list.Value!);
            Assert.True((await git.StashPopAsync(repo)).Ok);
            Assert.True((await git.GetStatusAsync(repo)).Value!.IsDirty);
        }
        finally { TryDeleteRepo(repo); }
    }
```

- [ ] **Step 3: Run to verify it fails** — FAIL.

- [ ] **Step 4: Implement** in `GitService`:
```csharp
    public async Task<GitResult> StashAsync(string worktreePath, string? message, CancellationToken ct = default)
        => ToResult(string.IsNullOrWhiteSpace(message)
            ? await RunAsync(worktreePath, ct, "stash", "push").ConfigureAwait(false)
            : await RunAsync(worktreePath, ct, "stash", "push", "-m", message).ConfigureAwait(false));

    public async Task<GitResult> StashPopAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "stash", "pop").ConfigureAwait(false));

    public async Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ct, "stash", "list", "--format=%gd: %gs").ConfigureAwait(false);
        if (!r.Ok) return GitResult<IReadOnlyList<string>>.Fail(r.Stderr);
        var list = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        return GitResult<IReadOnlyList<string>>.Success(list);
    }
```
Add `IGitStash` to the class base list.

- [ ] **Step 5: Run + commit** — test → PASS (or skips); build clean.

```bash
git add src/Styloagent.Git/IGitStash.cs src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): IGitStash save/pop/list

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Stash UI + VM

**Files:**
- Modify: `src/Styloagent.App/ViewModels/ChangesViewModel.cs`, `src/Styloagent.App/Views/ChangesView.axaml`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`, `src/Styloagent.App/App.axaml.cs`
- Test: `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitStash` (Task 4).
- Produces on `ChangesViewModel`: ctor gains `IGitStash stash`; `ObservableCollection<string> Stashes`; `[RelayCommand] Task Stash()` (stash with the `CommitMessage` as the optional label, or empty; reload + refresh stash list); `[RelayCommand] Task StashPop()` (pop + reload); `LoadStashesAsync()` from `LoadAsync`.

- [ ] **Step 1: Write the failing test** — `FakeStash : IGitStash` (records calls; list returns one). Assert: `LoadAsync` fills `Stashes`; `StashAsync()` calls `stash.StashAsync`; `StashPop()` calls `stash.StashPopAsync`; failures surface via `WriteError` (Task 1's `Report`). Update ctor call sites to the new arity.

- [ ] **Step 2: Run to verify it fails** — FAIL.

- [ ] **Step 3: Implement** — extend `ChangesViewModel` (Stashes + Stash/StashPop commands, each `Report`ing + reloading + refreshing the stash list). Wire `IGitStash` through `MainWindowViewModel`/`App.axaml.cs` (shared GitService cast). `ChangesView.axaml`: add a compact **stash** row — a **Stash** button, a **Pop** button, and (if any) a small stash-count/list indicator bound to `Stashes`.

- [ ] **Step 4: Run + commit** — focused test → PASS; full App.Tests → green; build clean.

```bash
git add src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/Views/ChangesView.axaml src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/App.axaml.cs tests/Styloagent.App.Tests/ChangesViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): stash save/pop + list in the Changes panel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Full-suite green + panel screenshot

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** run Core, App, Git, UITests suites → all pass.
- [ ] **Step 3:** the `ChangesWriteViewTests` render (or a new one) shows the branch bar + error banner (drive a failing fake) + stash controls; inspect `/tmp/styloagent-changes.png`.
- [ ] **Step 4:** commit any incidental fixes with the standard trailer.

---

## Self-Review

**Coverage:**
- Error surfacing (Plan 2d's logged Important gap) → Task 1 (`WriteError` + banner; every write op `Report`s). ✓
- Branch list/current/create/switch → Tasks 2-3. ✓
- Stash save/pop/list → Tasks 4-5. ✓
- Human panel actions only, no MCP → confirmed (no `FleetTools` changes). ✓
- **Deferred:** the double-`GetStatusAsync` consolidation (Plan 2c/2d note) — still a Minor optimization; the panel now fetches status (badge + changes) and branches + stashes on refresh, so a future consolidation pass could batch these; note it, don't fix here (correctness is fine, it's fire-and-forget off the UI thread). Interactive rebase / cherry-pick / blame remain out of scope.

**Deviation (intentional):** `IGitBranch`/`IGitStash` are new seams on `Styloagent.Git` (like `IGitLog`/`IGitDiff`/`IGitWrite`), keeping Core free of them; `GitBranch` is a UI-free Core record. `ChangesViewModel`'s ctor grows to `(git, diff, write, branch, stash)` — all satisfied by the single shared `GitService` instance via interface casts.

**Placeholder scan:** the model, seams, GitService methods, and VM error-reporting are complete code; the UI tasks specify the bars/controls and `[RelayCommand]` bindings. The `ListBranchesAsync` two-call approach (`--show-current` + `for-each-ref`) is concrete.

**Type consistency:** `GitBranch(Name, IsCurrent)` (Task 2) used by `IGitBranch`, the VM `Branches`/`CurrentBranch` (Task 3), and the view; `IGitStash`'s three methods (Task 4) consumed by the VM (Task 5); `WriteError`/`Report` (Task 1) used by every write op across Tasks 1/3/5.
