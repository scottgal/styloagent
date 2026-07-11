# Git Write Ops — Stage / Commit / Push / Pull (Plan 2d) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the read-mostly Git panel into a usable client for the daily inner loop — the human can stage/unstage an agent's changes, write a commit, and push/pull — all from the Changes panel, backed by the `git` CLI.

**Architecture:** The status model gains staged/unstaged tracking (porcelain `X`=index, `Y`=worktree). A new `IGitWrite` seam on `Styloagent.Git` (mirroring `IGitLog`/`IGitDiff`) exposes `StageAsync`/`UnstageAsync`/`CommitAsync`/`PushAsync`/`PullAsync` over `GitService`'s `RunAsync` (which uses `ProcessStartInfo.ArgumentList`, so `commit -m <multiline message>` is shell-safe without temp files). `ChangesViewModel` splits files into staged/unstaged sections with stage/unstage/commit/push/pull commands and self-reloads after each; the Plan 2c `.git` watcher is the backstop refresh. These are HUMAN panel actions — no new MCP tools (agents commit via their terminals).

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, xUnit. Git CLI ≥ 2.25.1 (`git restore --staged`).

## Global Constraints

- net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build clean (0 warnings/0 errors; pre-existing NU1903 Tmds.DBus warnings are not ours).
- Git access never throws across the seam — write ops return `GitResult` carrying git's stderr on failure (mirrors the existing `IGitService`/`GitService`).
- ConfigureAwait: new `GitService` write methods reuse the private `RunAsync` (already `.ConfigureAwait(false)`); VM write commands are awaited (not blocked-on) and set UI-bound state, so they do NOT add `.ConfigureAwait(false)`.
- Use `ArgumentList` (no shell): pass the commit message and file paths as single argv elements — never build a shell string.
- Commit directly to `main` (no new branch), authored `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` + trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Modify:**
- `src/Styloagent.Core/Git/GitStatus.cs` — `GitChange` gains `Staged`/`Unstaged`.
- `src/Styloagent.Core/Git/GitStatusParser.cs` — set staged/unstaged from `X`/`Y`.
- `src/Styloagent.Git/GitService.cs` — implement `IGitWrite`.
- `src/Styloagent.App/ViewModels/ChangesViewModel.cs` — staged/unstaged sections + write commands.
- `src/Styloagent.App/Views/ChangesView.axaml` — two sections + commit box + action buttons.
- `src/Styloagent.App/App.axaml.cs` — pass the shared `GitService` as `IGitWrite` (or via the existing shared-instance cast).
- Call sites/tests that construct `GitChange` (parser tests, `ChangesViewModelTests`, `GitPanelRefreshTests` fakes) — add the two new positional args.

**Create:**
- `src/Styloagent.Git/IGitWrite.cs` — the write seam.
- Tests: extend `GitStatusParserTests`, `GitServiceIntegrationTests`, `ChangesViewModelTests`; add `tests/Styloagent.UITests/ChangesWriteViewTests.cs`.

---

## Task 1: Staged/unstaged in the status model

**Files:**
- Modify: `src/Styloagent.Core/Git/GitStatus.cs`, `src/Styloagent.Core/Git/GitStatusParser.cs`
- Modify (call sites): any `new GitChange(...)` in tests (`GitStatusParserTests`, `ChangesViewModelTests`, `GitPanelRefreshTests`)
- Test: `tests/Styloagent.Core.Tests/GitStatusParserTests.cs`

**Interfaces:**
- Produces: `GitChange(string Path, GitChangeKind Kind, bool Staged, bool Unstaged)`. Parser rule (porcelain v2 `1 <XY>`/`2 <XY>`): `Staged = X != '.'`, `Unstaged = Y != '.'`. Untracked (`?`) → `Staged=false, Unstaged=true`. Unmerged (`u`) → `Staged=false, Unstaged=true` (conflict is a worktree concern).

- [ ] **Step 1: Write the failing test** — add to `GitStatusParserTests.cs`:

```csharp
    [Fact]
    public void Parse_tracks_staged_and_unstaged()
    {
        // "1 M. …" = staged modify (X=M, Y=.); "1 .M …" = unstaged modify; "? new" = untracked (unstaged)
        var s = GitStatusParser.Parse(
            "# branch.ab +0 -0\n" +
            "1 M. N... 100644 100644 100644 aaa bbb staged.cs\n" +
            "1 .M N... 100644 100644 100644 aaa bbb unstaged.cs\n" +
            "? untracked.cs\n");
        Assert.Contains(s.Changes, c => c.Path == "staged.cs" && c.Staged && !c.Unstaged);
        Assert.Contains(s.Changes, c => c.Path == "unstaged.cs" && !c.Staged && c.Unstaged);
        Assert.Contains(s.Changes, c => c.Path == "untracked.cs" && !c.Staged && c.Unstaged);
    }
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitStatusParserTests"` → FAIL (GitChange has no `Staged`).

- [ ] **Step 3: Implement**

`GitStatus.cs` — extend the record:
```csharp
public sealed record GitChange(string Path, GitChangeKind Kind, bool Staged, bool Unstaged);
```

`GitStatusParser.cs` — set the flags. For `1`/`2` lines, `xy[0]` is X (index/staged), `xy[1]` is Y (worktree/unstaged):
```csharp
            if (line[0] == '1' || line[0] == '2')
            {
                var xy = line.Length >= 4 ? line.Substring(2, 2) : "..";
                bool staged = xy[0] != '.';
                bool unstaged = xy[1] != '.';
                changes.Add(new GitChange(PathOf(line), KindFromXy(xy, renamed: line[0] == '2'), staged, unstaged));
            }
```
And the untracked + conflicted lines:
```csharp
            if (line[0] == '?') { changes.Add(new GitChange(line[2..].Trim(), GitChangeKind.Untracked, false, true)); continue; }
            if (line[0] == 'u') { hasConflicts = true; changes.Add(new GitChange(PathOf(line), GitChangeKind.Conflicted, false, true)); continue; }
```

Update every OTHER `new GitChange(...)` construction (search the solution): the fakes/fixtures in `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`, `tests/Styloagent.App.Tests/GitPanelRefreshTests.cs`, and any test building a `GitChange` — add `Staged`/`Unstaged` args (e.g. `new GitChange("a.txt", GitChangeKind.Modified, false, true)`). The compiler will point out each site.

- [ ] **Step 4: Run to verify it passes** — the parser test → PASS; then full Core.Tests + App.Tests → green (fix any `GitChange` construction the compiler flags).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/GitStatus.cs src/Styloagent.Core/Git/GitStatusParser.cs tests/
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): track staged/unstaged per change in the status model

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `IGitWrite` seam — Stage / Unstage / Commit

**Files:**
- Create: `src/Styloagent.Git/IGitWrite.cs`
- Modify: `src/Styloagent.Git/GitService.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`

**Interfaces:**
- Produces: `interface IGitWrite` (namespace `Styloagent.Git`) with
  `Task<GitResult> StageAsync(string worktreePath, string relativePath, CancellationToken ct = default)`,
  `Task<GitResult> UnstageAsync(string worktreePath, string relativePath, CancellationToken ct = default)`,
  `Task<GitResult> CommitAsync(string worktreePath, string message, CancellationToken ct = default)`,
  `Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default)`,
  `Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default)`.
  `GitService : …, IGitWrite`. (Push/Pull implemented in Task 3, but declare the whole interface here and stub Push/Pull to `NotImplemented`-free by implementing them in Task 3 — OR declare Push/Pull in Task 3. To keep the interface stable, DECLARE all five here and implement Stage/Unstage/Commit now, Push/Pull in Task 3.)

- [ ] **Step 1: Create `IGitWrite.cs`** with all five methods (above).

- [ ] **Step 2: Write the failing integration test** — add to `GitServiceIntegrationTests.cs` (reuses `GitAvailable()`/`Run()`/`TryDeleteRepo`, skips without git):

```csharp
    [Fact]
    public async Task Stage_commit_round_trip()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.StageAsync(repo, "a.txt")).Ok);

            var staged = await git.GetStatusAsync(repo);
            Assert.Contains(staged.Value!.Changes, c => c.Path == "a.txt" && c.Staged);

            Assert.True((await git.CommitAsync(repo, "line 1\nline 2")).Ok);   // multiline message
            var afterCommit = await git.GetStatusAsync(repo);
            Assert.False(afterCommit.Value!.IsDirty);
        }
        finally { TryDeleteRepo(repo); }
    }
```

- [ ] **Step 3: Run to verify it fails** — FAIL (methods missing).

- [ ] **Step 4: Implement in GitService** — add `IGitWrite` to the class base list + the three methods (reuse `RunAsync`, `.ConfigureAwait(false)`; args via `ArgumentList` so paths/messages are single argv elements):
```csharp
    public async Task<GitResult> StageAsync(string worktreePath, string relativePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "add", "--", relativePath).ConfigureAwait(false));

    public async Task<GitResult> UnstageAsync(string worktreePath, string relativePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "restore", "--staged", "--", relativePath).ConfigureAwait(false));

    public async Task<GitResult> CommitAsync(string worktreePath, string message, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "commit", "-m", message).ConfigureAwait(false));
```
(`ToResult(ProcOutcome)` already exists in GitService; reuse it. If it's private and returns `GitResult`, these compile as-is.)

- [ ] **Step 5: Run + commit** — integration test → PASS (or skips); `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Git/IGitWrite.cs src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): IGitWrite stage/unstage/commit over the git CLI

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Push / Pull

**Files:**
- Modify: `src/Styloagent.Git/GitService.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`

**Interfaces:**
- Consumes/implements the `PushAsync`/`PullAsync` declared on `IGitWrite` (Task 2).

- [ ] **Step 1: Write the failing integration test** — a local bare remote round-trip (skips without git):

```csharp
    [Fact]
    public async Task Push_to_a_local_bare_remote()
    {
        if (!GitAvailable()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitpush-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "remote.git");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(bare); Directory.CreateDirectory(work);
        try
        {
            Run(bare, "init --bare -b main");
            Run(work, "init -b main"); Run(work, "config user.email t@t.t"); Run(work, "config user.name t");
            File.WriteAllText(Path.Combine(work, "a.txt"), "one\n"); Run(work, "add -A"); Run(work, "commit -m init");
            Run(work, $"remote add origin \"{bare}\"");
            Run(work, "push -u origin main");
            File.WriteAllText(Path.Combine(work, "a.txt"), "two\n"); Run(work, "commit -am second");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.PushAsync(work)).Ok, "push should succeed to the configured upstream");
        }
        finally { TryDeleteRepo(root); }
    }
```

- [ ] **Step 2: Run to verify it fails** — FAIL (PushAsync missing).

- [ ] **Step 3: Implement** in GitService:
```csharp
    public async Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "push").ConfigureAwait(false));

    public async Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "pull", "--no-edit").ConfigureAwait(false));
```

- [ ] **Step 4: Run + commit** — test → PASS (or skips); build clean.

```bash
git add src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): IGitWrite push/pull

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `ChangesViewModel` — staged/unstaged sections + write commands

**Files:**
- Modify: `src/Styloagent.App/ViewModels/ChangesViewModel.cs`
- Test: `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitWrite` (Tasks 2-3), `IGitService`/`IGitDiff` (existing), `GitChange.Staged/Unstaged` (Task 1).
- Produces: `ChangesViewModel(IGitService git, IGitDiff diff, IGitWrite write)`; `ObservableCollection<GitChange> StagedFiles`/`UnstagedFiles`; `[ObservableProperty] string CommitMessage`; `bool CanCommit => StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage)`; awaitable `StageAsync(GitChange)`, `UnstageAsync(GitChange)`, `CommitAsync()`, `PushAsync()`, `PullAsync()` — each runs the git op then re-`LoadAsync(_worktreePath)`; `CommitAsync` also clears `CommitMessage` on success.

- [ ] **Step 1: Write the failing test** — extend `ChangesViewModelTests.cs`. Add a `FakeWrite : IGitWrite` recording calls and returning `GitResult.Success()`. Have `FakeGit.GetStatusAsync` return a status with one staged + one unstaged change. Assert: after `LoadAsync`, `StagedFiles.Count==1` and `UnstagedFiles.Count==1`; `await StageAsync(unstagedChange)` calls `write.StageAsync` with the path and reloads; `CanCommit` is false with empty message and true with a staged file + message; `await CommitAsync()` calls `write.CommitAsync` with `CommitMessage` and clears it.

```csharp
    private sealed class FakeWrite : Styloagent.Git.IGitWrite
    {
        public string? LastStaged, LastCommitMsg;
        public Task<GitResult> StageAsync(string w, string p, CancellationToken ct = default) { LastStaged = p; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> UnstageAsync(string w, string p, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> CommitAsync(string w, string m, CancellationToken ct = default) { LastCommitMsg = m; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> PushAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> PullAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }
```
(Adjust the existing `FakeGit`/`FakeDiff` to the new `GitChange` arity and the new ctor signature.)

- [ ] **Step 2: Run to verify it fails** — FAIL.

- [ ] **Step 3: Implement** `ChangesViewModel`:
- ctor takes `IGitWrite write`.
- Add `StagedFiles`/`UnstagedFiles` `ObservableCollection<GitChange>`; in `LoadAsync`, after fetching status, clear+refill: `Files` (union, keep for the existing selection binding), `StagedFiles` (where `Staged`), `UnstagedFiles` (where `Unstaged`).
- Add `[ObservableProperty][NotifyPropertyChangedFor(nameof(CanCommit))] private string _commitMessage = "";` and `public bool CanCommit => StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);` (raise `CanCommit` on `StagedFiles` changes too — call `OnPropertyChanged(nameof(CanCommit))` at the end of `LoadAsync`).
- `StageAsync(GitChange c)`: `await _write.StageAsync(_worktreePath, c.Path); await LoadAsync(_worktreePath);`. `UnstageAsync` similarly with `_write.UnstageAsync`. `CommitAsync()`: if `!CanCommit` return; `var r = await _write.CommitAsync(_worktreePath, CommitMessage); if (r.Ok) CommitMessage = ""; await LoadAsync(_worktreePath);`. `PushAsync()`/`PullAsync()`: run the op then `await LoadAsync(_worktreePath)` (to refresh ahead/behind).
- `Clear()` also clears `StagedFiles`/`UnstagedFiles` and `CommitMessage`.

- [ ] **Step 4: Update `App.axaml.cs`** — `ChangesViewModel` now needs `IGitWrite`; the shared `GitService` implements it, so pass it (via the same shared-instance pattern used for `IGitDiff` — cast/inject the shared instance). Update `MainWindowViewModel` where `Changes` is constructed to pass the write seam.

- [ ] **Step 5: Run + commit** — focused test → PASS; full App.Tests → green; `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/App.axaml.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/ChangesViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): staged/unstaged sections + stage/commit/push/pull commands

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Changes panel UI — sections, commit box, action buttons

**Files:**
- Modify: `src/Styloagent.App/Views/ChangesView.axaml`
- Test: `tests/Styloagent.UITests/ChangesWriteViewTests.cs`

**Interfaces:**
- Consumes: `ChangesViewModel` (Task 4).

- [ ] **Step 1: Restructure `ChangesView.axaml`** — replace the single files list with:
  - An **Unstaged** section: header + `ItemsControl`/`ListBox` over `UnstagedFiles`; each row shows the `GitChangeKind` + `Path` and a small **Stage** button (`Command` bound to a method that calls `StageAsync(change)` — use a code-behind handler or a `RelayCommand` with the item as parameter). Clicking a row still loads its diff (keep `SelectFileAsync`).
  - A **Staged** section: header + list over `StagedFiles`, each row with an **Unstage** button.
  - A **commit bar**: a multiline `TextBox` bound to `CommitMessage` + a **Commit** button (`IsEnabled="{Binding CanCommit}"`, command → `CommitAsync`).
  - An **actions row**: **Push** / **Pull** buttons (commands → `PushAsync`/`PullAsync`).
  - The hosted `DiffView` (`DataContext="{Binding Diff}"`) still shows the selected file's diff.
  Mirror `IssuesView`/existing `ChangesView` styling + theme tokens. Keep it readable in both themes (use theme brushes, not literal hexes).
- The stage/unstage/commit/push/pull commands: since `ChangesViewModel`'s methods are awaitable `Task` methods, expose them as `[RelayCommand]` (CommunityToolkit generates `StageCommand` etc.) OR bind buttons to code-behind handlers. Prefer `[RelayCommand]` on `ChangesViewModel` (annotate `StageAsync`→`[RelayCommand] private async Task Stage(GitChange c)`, etc.) so the XAML binds `Command="{Binding StageCommand}" CommandParameter="{Binding}"`. Adjust Task 4's method names to the `[RelayCommand]` convention if you choose this (keep the awaitable methods callable by tests — `[RelayCommand]` generates a command that wraps the method; tests can call the method directly if it stays public, or call `StageCommand.ExecuteAsync(c)`).

- [ ] **Step 2: Write the render test** — `tests/Styloagent.UITests/ChangesWriteViewTests.cs` mirroring `IssuesViewTests`: build a `ChangesViewModel` with fakes (one staged + one unstaged file), `await LoadAsync`, host `ChangesView`, `SettleAsync`, assert the Unstaged and Staged section headers + a Stage button render, screenshot `/tmp/styloagent-changes.png`.

- [ ] **Step 3: Run + commit** — render test → PASS; inspect `/tmp/styloagent-changes.png` shows the two sections + commit bar + buttons.

```bash
git add src/Styloagent.App/Views/ChangesView.axaml src/Styloagent.App/Views/ChangesView.axaml.cs tests/Styloagent.UITests/ChangesWriteViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): Changes panel — stage/unstage/commit/push/pull UI

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Full-suite green + changes screenshot

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** run Core, App, Git, UITests suites → all pass.
- [ ] **Step 3:** inspect `/tmp/styloagent-changes.png` — staged/unstaged sections + commit bar + push/pull render, readable.
- [ ] **Step 4:** commit any incidental fixes with the standard trailer.

---

## Self-Review

**Spec coverage (Plan 2d slice of the git-client spec):**
- Stage/unstage/commit (the inner loop) → Tasks 1, 2, 4, 5. ✓
- Push/pull (remote sync) → Tasks 3, 4, 5. ✓
- Staged vs unstaged distinction the UI needs → Task 1 (model) + Task 4/5 (sections). ✓
- `ArgumentList`-safe commit messages (multiline) → Task 2 (verified by the multiline-message integration test). ✓
- Human panel actions, not agent tools → no MCP changes. ✓
- **Deferred to Plan 2e:** branch create/switch + stash save/pop/list (more UI: branch picker, stash list). The double-`GetStatusAsync` consolidation logged in Plan 2c can also fold in here.

**Deviation (intentional):** `IGitWrite` is a new seam on `Styloagent.Git` (like `IGitLog`/`IGitDiff`), keeping `Styloagent.Core` free of it; the write ops take `GitResult` from Core (a value wrapper, not a UI type). `GitChange` gains two bools in Core (UI-free) — the parser is the only producer.

**Placeholder scan:** the model, parser, and GitService methods are complete code; the VM/UI tasks give exact command names and the `[RelayCommand]` wiring choice. The one flagged decision (RelayCommand vs code-behind for the buttons) is resolved in Task 5 (prefer `[RelayCommand]`).

**Type consistency:** `GitChange(Path, Kind, Staged, Unstaged)` (Task 1) is used by the parser, VM sections, and fakes; `IGitWrite`'s five methods (Tasks 2-3) are consumed by `ChangesViewModel` (Task 4) and bound in the view (Task 5); `CanCommit`/`CommitMessage`/`StagedFiles`/`UnstagedFiles` (Task 4) are bound in Task 5.
