# Git Panel Refresh Spine + Polish (Plan 2c) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the assembled Git panel (History + Changes + badge) always reflect the selected agent's real worktree state — clearing on worktree-less panes, refreshing after wrap-up, and auto-updating when the agent commits — and fix the light-theme diff readability. This is the reactive glue the three prior git plans each assume but none own; it must land before the write ops (Plan 2d).

**Architecture:** A single `RefreshGitPanelFor(pane)` on `MainWindowViewModel` is the one place that (re)loads the graph, the changes list, and the badge — or clears them when the pane has no worktree. It's called from selection, after wrap-up, and by a debounced `FileSystemWatcher` on the selected worktree's `.git`. The diff row colours move to theme-aware brushes.

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build clean (0 warnings/0 errors; pre-existing NU1903 Tmds.DBus warnings are not ours).
- Fire-and-forget refresh calls only (`_ = …`) — never block the UI thread on git work. The underlying `GitService`/`WrapUpService` already use `.ConfigureAwait(false)`; the VM refresh awaits set UI-bound properties, so they must NOT add `.ConfigureAwait(false)`.
- Reuse existing pieces: `AgentPaneViewModel.RefreshGitStatusAsync(IGitService)` already blanks the badge when `WorktreePath` is null; `GitGraphViewModel`/`ChangesViewModel` already null/clear their state inside `LoadAsync` on empty/failure.
- Commit directly to `main` (no new branch), authored `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` + trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Modify:**
- `src/Styloagent.App/ViewModels/GitGraphViewModel.cs` — add `Clear()`.
- `src/Styloagent.App/ViewModels/ChangesViewModel.cs` — add `Clear()`.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — add `RefreshGitPanelFor(pane)`; call it from `OnSelectedPaneChanged` (both branches), from `WrapUp` (after merge), and from the watcher; add the `.git` watcher lifecycle.
- `src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs` — theme-aware brushes.
- `src/Styloagent.App/Themes/ThemeTokens.axaml` — diff row brushes (Dark + Light).

**Create:**
- `src/Styloagent.App/Services/WorktreeGitWatcher.cs` — debounced `.git` FileSystemWatcher.
- Tests: `tests/Styloagent.App.Tests/GitPanelRefreshTests.cs`, `tests/Styloagent.UITests/DiffViewLightThemeTests.cs`.

---

## Task 1: VM `Clear()` methods + `RefreshGitPanelFor` spine

**Files:**
- Modify: `src/Styloagent.App/ViewModels/GitGraphViewModel.cs`, `src/Styloagent.App/ViewModels/ChangesViewModel.cs`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/GitPanelRefreshTests.cs`

**Interfaces:**
- Produces: `GitGraphViewModel.Clear()` (Graph=null, Layout=null, CommitCount=0); `ChangesViewModel.Clear()` (Files cleared, Diff.File=null, worktree path reset); `MainWindowViewModel.RefreshGitPanelFor(AgentPaneViewModel? pane)` (public for test) — when `pane?.WorktreePath` is null → clears graph+changes and refreshes the badge (which blanks it); else → loads graph+changes for the path and refreshes the badge.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.App.Tests/GitPanelRefreshTests.cs`. Test the two VM `Clear()` methods directly (they are trivially unit-testable) and, if the existing VM test scaffold supports it, that `RefreshGitPanelFor` clears when the pane has no worktree:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

public class GitPanelRefreshTests
{
    private sealed class FakeLog : IGitLog
    {
        public Task<GitResult<System.Collections.Generic.IReadOnlyList<Commit>>> GetCommitsAsync(string w, int limit = 200, CancellationToken ct = default)
        {
            System.Collections.Generic.IReadOnlyList<Commit> c = new[] { new Commit { SHA = "a", Color = 0 } };
            return Task.FromResult(GitResult<System.Collections.Generic.IReadOnlyList<Commit>>.Success(c));
        }
    }

    [Fact]
    public async Task GitGraph_Clear_blanks_the_graph()
    {
        var vm = new GitGraphViewModel(new FakeLog());
        await vm.LoadAsync("/wt");
        Assert.NotNull(vm.Graph);
        vm.Clear();
        Assert.Null(vm.Graph);
        Assert.Equal(0, vm.CommitCount);
    }
}
```
(Add an analogous `ChangesViewModel.Clear` test using the `FakeGit`/`FakeDiff` from the existing `ChangesViewModelTests.cs` — load two files, `Clear()`, assert `Files` empty and `Diff.File` null.)

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~GitPanelRefreshTests"` → FAIL (`Clear` missing).

- [ ] **Step 3: Implement**

`GitGraphViewModel.cs` — add:
```csharp
    /// <summary>Blanks the graph (no worktree selected).</summary>
    public void Clear()
    {
        Graph = null;
        Layout = null;
        CommitCount = 0;
    }
```

`ChangesViewModel.cs` — add:
```csharp
    /// <summary>Clears the file list and diff (no worktree selected).</summary>
    public void Clear()
    {
        _worktreePath = string.Empty;
        SelectedFile = null;
        Files.Clear();
        Diff.File = null;
    }
```

`MainWindowViewModel.cs` — add the spine method and call it from `OnSelectedPaneChanged`:
```csharp
    /// <summary>
    /// Single place that re-syncs the Git panel (History + Changes + roster badge) to a pane's
    /// worktree — clearing everything when the pane has no worktree. Fire-and-forget; never blocks.
    /// </summary>
    public void RefreshGitPanelFor(AgentPaneViewModel? pane)
    {
        if (pane?.WorktreePath is { } path)
        {
            if (GitGraph is not null) _ = GitGraph.LoadAsync(path);
            if (Changes is not null) _ = Changes.LoadAsync(path);
        }
        else
        {
            GitGraph?.Clear();
            Changes?.Clear();
        }
        if (pane is not null && _git is not null) _ = pane.RefreshGitStatusAsync(_git);
    }
```
Replace the `OnSelectedPaneChanged` worktree block (currently lines ~694-698) with a single call:
```csharp
    partial void OnSelectedPaneChanged(AgentPaneViewModel? oldValue, AgentPaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        RefreshGitPanelFor(newValue);
    }
```

- [ ] **Step 4: Run to verify it passes** — focused test → PASS; full App.Tests → green; `dotnet build src/Styloagent.App -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/GitGraphViewModel.cs src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/GitPanelRefreshTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): RefreshGitPanelFor spine — panel clears on worktree-less panes

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: WrapUp refreshes the panel + badge

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/GitPanelRefreshTests.cs` (extend if the WrapUp path is testable with the existing scaffold; otherwise a focused assertion that WrapUp on a selected merged pane leaves the panel cleared)

**Interfaces:**
- Consumes: `RefreshGitPanelFor` (Task 1).

- [ ] **Step 1: Write/adjust the test** — if a full `WrapUp` VM test is impractical with the current scaffold, add a focused test that after nulling a selected pane's worktree and calling `RefreshGitPanelFor(pane)`, the graph/changes are cleared (this exercises the same post-merge code path). Keep it deterministic.

- [ ] **Step 2: Implement** — in `WrapUp`, after the merge-cleanup block, refresh the panel for the wrapped-up pane:
```csharp
        Issues?.Refresh();
        if (outcome.Merged)
        {
            pane.WorktreePath = null;
            pane.WorktreeBranch = null;
        }
        RefreshGitPanelFor(pane);   // blanks graph/changes/badge for the now-worktree-less pane
        return outcome;
```
(Since `RefreshGitPanelFor` refreshes the badge and — because `WorktreePath` is now null — clears graph+changes, this fixes the stale-after-wrap-up gap for the common case where the wrapped pane is selected. It is safe even if the pane is not the selected one: the graph/changes VMs are shared singletons showing the *selected* pane, so clearing them when wrapping up the selected pane is correct; when wrapping up a non-selected pane, `RefreshGitPanelFor(pane)` still correctly blanks that pane's badge and the panel reflects `pane` — acceptable, and re-selecting another pane re-syncs via Task 1.)

> Note: if wrapping up a NON-selected pane should leave the selected pane's panel untouched, guard the graph/changes part with `if (pane == SelectedPane)`. Implement that guard: only clear/reload graph+changes when `pane == SelectedPane`; always refresh `pane`'s badge. Update `RefreshGitPanelFor` to take an optional `bool panelToo = true` OR add a small `RefreshBadgeFor(pane)` for the non-selected case. Choose the cleaner of: (a) `if (pane == SelectedPane) RefreshGitPanelFor(pane); else _ = pane.RefreshGitStatusAsync(_git);` in WrapUp. Prefer (a) — it's the least code and correctly scopes panel vs badge.

Apply option (a): replace the single `RefreshGitPanelFor(pane)` line above with:
```csharp
        if (pane == SelectedPane) RefreshGitPanelFor(pane);
        else if (_git is not null) _ = pane.RefreshGitStatusAsync(_git);
```

- [ ] **Step 3: Run + commit** — focused test → PASS; full App.Tests → green.

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/GitPanelRefreshTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "fix(git): refresh panel + badge after wrap-up removes a worktree

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Debounced `.git` watcher → live panel refresh

**Files:**
- Create: `src/Styloagent.App/Services/WorktreeGitWatcher.cs`
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` (own the watcher; point it at the selected worktree; dispose)
- Test: `tests/Styloagent.App.Tests/WorktreeGitWatcherTests.cs`

**Interfaces:**
- Produces: `WorktreeGitWatcher` — watches a worktree's `.git` (HEAD + index) and raises a debounced `Changed` event; `Watch(string? worktreePath)` (null stops watching); `IDisposable`. The debounce coalesces bursts (e.g. 300 ms) and marshals nothing itself (the VM marshals to the UI thread).

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.App.Tests/WorktreeGitWatcherTests.cs`: point a watcher at a temp dir containing a `.git` folder, subscribe to `Changed`, write to `.git/HEAD`, and assert the event fires within a timeout (use a `TaskCompletionSource` + `SemaphoreSlim`/`ManualResetEventSlim` with a generous timeout, e.g. 3 s; skip/no-op cleanly if the platform's FileSystemWatcher is unavailable). Assert the debounce coalesces two rapid writes into (at least) one event.

- [ ] **Step 2: Run to verify it fails** — `dotnet test ... --filter "FullyQualifiedName~WorktreeGitWatcherTests"` → FAIL.

- [ ] **Step 3: Implement** `WorktreeGitWatcher.cs`:
- A `FileSystemWatcher` on `<worktreePath>/.git` (note: for a linked worktree, `.git` is a FILE pointing at the real gitdir; watch the worktree root's `.git` path — HEAD/index live under the worktree's gitdir; if `.git` is a file, read the `gitdir:` line to find the real dir and watch that, falling back to watching the worktree root non-recursively). Keep it tolerant: never throw; if the path can't be watched, `Watch` is a no-op.
- Debounce with a timer (reset on each raw FS event; fire `Changed` once after the quiet period). Do NOT use `Date.Now`/timers that break tests — use `System.Threading.Timer` with a fixed interval.
- `Watch(string? path)` disposes any current watcher and starts a new one (or stops if null). `Dispose()` stops.

- [ ] **Step 4: Wire into the VM** — `MainWindowViewModel` holds a `WorktreeGitWatcher _gitWatcher` (created in `InitializeAsync`, subscribed so `Changed` → `Dispatcher.UIThread.Post(() => RefreshGitPanelFor(SelectedPane))`). In `RefreshGitPanelFor` (or `OnSelectedPaneChanged`), call `_gitWatcher.Watch(pane?.WorktreePath)` so the watcher always tracks the selected worktree. Dispose the watcher in the VM's `Dispose()`.

- [ ] **Step 5: Run + commit** — focused test → PASS (or skips cleanly); full App.Tests → green; `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.App/Services/WorktreeGitWatcher.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/WorktreeGitWatcherTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): debounced .git watcher refreshes the panel on agent commits

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Theme-aware diff row colours (light-theme fix)

**Files:**
- Modify: `src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs`, `src/Styloagent.App/Themes/ThemeTokens.axaml`
- Test: `tests/Styloagent.UITests/DiffViewLightThemeTests.cs`

**Interfaces:**
- The converter returns a readable row background for BOTH themes; `DiffView`'s content foreground (`PrimaryTextBrush`, theme-swapping) reads on it in light and dark.

- [ ] **Step 1: Add theme brushes** — in `ThemeTokens.axaml`, add to BOTH the Dark and Light `ThemeDictionaries` three brushes:
  - Dark: `DiffAddedBgBrush=#1E3A24`, `DiffDeletedBgBrush=#3A1E1E`, `DiffHeaderBgBrush=#2A2A3A` (the current values — dark bg, light `PrimaryTextBrush` text reads fine).
  - Light: `DiffAddedBgBrush=#D6F0DD` (pale green), `DiffDeletedBgBrush=#F5D6D6` (pale red), `DiffHeaderBgBrush=#E6E6EE` (pale grey) — so the light-theme `PrimaryTextBrush` (`#1A1A2E`, near-black) reads on them.

- [ ] **Step 2: Make the converter theme-aware** — `DiffLineKindBrushConverter.Convert` reads the current theme and returns the matching brush. Because a value converter is theme-blind, resolve the current variant at convert time:
```csharp
public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
{
    var kind = value as DiffLineKind? ?? DiffLineKind.Context;
    if (kind == DiffLineKind.Context) return Brushes.Transparent;
    bool light = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
    return kind switch
    {
        DiffLineKind.Added   => light ? LightAdded   : DarkAdded,
        DiffLineKind.Deleted => light ? LightDeleted : DarkDeleted,
        DiffLineKind.Header  => light ? LightHeader  : DarkHeader,
        _ => Brushes.Transparent,
    };
}
```
with the six static brushes matching the ThemeTokens hexes above. (Re-selection or a panel refresh re-runs the converter, so a theme toggle is reflected on the next refresh — acceptable; note this in the report.)

- [ ] **Step 3: Write the render test** — `tests/Styloagent.UITests/DiffViewLightThemeTests.cs`: set `Application.Current.RequestedThemeVariant = ThemeVariant.Light` (mirror how the app's light-theme toggle works — see `MainWindowViewModel.OnIsLightThemeChanged`), render a `DiffView` with added/deleted lines, `SettleAsync`, screenshot `/tmp/styloagent-diff-light.png`, and assert the added row's resolved background is the pale-green brush (query the row `Border.Background` via the visual tree, or assert the converter returns the light brush for `DiffLineKind.Added` when the variant is Light — a direct converter unit test is the most robust assertion; the screenshot is for visual confirmation). Restore the variant after.

- [ ] **Step 4: Run + commit** — test → PASS; inspect `/tmp/styloagent-diff-light.png` shows dark text on pale rows (readable).

```bash
git add src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs src/Styloagent.App/Themes/ThemeTokens.axaml tests/Styloagent.UITests/DiffViewLightThemeTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "fix(git): theme-aware diff row colours (readable in light + dark)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Full-suite green + light/dark diff screenshots

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** run Core, App, Git, UITests suites → all pass.
- [ ] **Step 3:** inspect `/tmp/styloagent-diff.png` (dark) and `/tmp/styloagent-diff-light.png` (light) — both readable.
- [ ] **Step 4:** commit any incidental fixes with the standard trailer.

---

## Self-Review

**Integration-review coverage (this plan closes the findings):**
- Stale graph/changes on worktree-less pane → Task 1 (`RefreshGitPanelFor` else-branch clears; empty-states now reachable). ✓
- Stale panel + badge after wrap-up → Task 2 (refresh scoped to selected vs badge-only for non-selected). ✓
- Badge computed once at spawn / no live refresh → Task 1 (badge refresh in the spine, on every selection) + Task 3 (`.git` watcher for terminal commits). ✓
- Light-theme diff unreadable → Task 4 (theme-aware brushes). ✓
- **Deferred (right calls, not this plan):** `CommitGraph.SetDefaultPens` global statics (harmless single-window); the "retire agent from roster on merge" doc/behaviour reconciliation (a product decision — flag to the human, don't silently change); nested Git-tab `#9D7FE0` literal (cosmetic; swap to `AccentBrush` opportunistically in Task 4 if trivial); double `GetStatusAsync` consolidation (badge + changes both fetch status on refresh — a Minor optimization; note it, defer to Plan 2d which restructures the panel for write ops).

**Placeholder scan:** the spine/Clear methods + converter are complete code; the watcher task specifies the FileSystemWatcher lifecycle, the linked-worktree `.git`-is-a-file caveat, debounce, and tolerant no-throw behaviour concretely. The one flagged decision (WrapUp on a non-selected pane) is resolved in Task 2 with option (a).

**Type consistency:** `RefreshGitPanelFor(AgentPaneViewModel?)` is defined in Task 1 and consumed in Tasks 2-3; `GitGraphViewModel.Clear()`/`ChangesViewModel.Clear()` (Task 1) are used by the spine; the diff brushes (Task 4) match between `ThemeTokens.axaml` and the converter.
