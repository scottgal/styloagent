# Vendored Commit-Graph Panel (Plan 2a of 2b/2c) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Vendor SourceGit's commit-graph model + rendering control (MIT) into `Styloagent.Git`, feed it from our `IGitService` (`git log`), and show a read-only per-agent history graph in a new Git tab — the first visual piece of the full git client.

**Architecture:** Copy SourceGit's self-contained graph closure (`Models/CommitGraph.cs`, `Models/Commit.cs`+`User`+`Decorator`, `Views/CommitGraph.cs`) into `Styloagent.Git/Vendored/`, namespace-rewritten and stripped of app-coupled members. Add `IGitService.GetCommitsAsync` (a `git log` parser producing the vendored `Commit` list). A `GitGraphViewModel`/`GitGraphView` builds the graph via `CommitGraph.Generate(...)` and hosts the vendored control, coloured from our palette. This is the first of three vendored-client plans (2a graph → 2b diff/changes → 2c write ops + full panel).

**Tech Stack:** .NET 10, Avalonia 11.3.12 (NO bump needed — vendored source compiles against our Avalonia), CommunityToolkit.Mvvm, xUnit. Git CLI.

## Global Constraints

- Target framework net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build stays clean (0 warnings/0 errors; pre-existing NU1903 Tmds.DBus warnings are not ours).
- **Do NOT bump Avalonia.** SourceGit's graph model/control use only stable core Avalonia APIs (`Control`, `DirectProperty`, `StyledProperty`, `Avalonia.Media` `Color`/`Pen`/`Point`, `DrawingContext`); they compile against our 11.3.12. The Avalonia 11.3.18 bump belongs to Plan 2b (AvaloniaEdit), only if needed.
- **Licensing:** SourceGit is MIT. Every vendored file keeps a header comment: `// Vendored from SourceGit (https://github.com/sourcegit-scm/sourcegit), MIT. See Styloagent.Git/THIRD-PARTY.md`. `THIRD-PARTY.md` records the MIT licence text, the repo URL, and the vendored commit SHA.
- **Vendored namespace:** every vendored file is rewritten from `namespace SourceGit.Models` / `SourceGit.Views` to `namespace Styloagent.Git.Vendored.Models` / `Styloagent.Git.Vendored.Controls`. Strip members that reach SourceGit's App/Preferences/locale/ViewModels — the graph closure is self-contained, so this is limited to a few presentation extras (noted per task).
- `Styloagent.Git` gains Avalonia package references in this plan (the vendored model uses `Avalonia.Media`, the control uses Avalonia). Add the SAME Avalonia version already used across the solution (11.3.12) — read an existing csproj (e.g. `src/Styloagent.App/Styloagent.App.csproj`) for the exact `Avalonia` package version/line and mirror it.
- Commit directly to `main` (no new branch), authored:
  `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` + trailer line `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- The vendored SourceGit source to copy from lives at `/tmp/sourcegit-src` (shallow clone). If absent, re-clone: `git clone --depth 1 https://github.com/sourcegit-scm/sourcegit.git /tmp/sourcegit-src`, and record its `git -C /tmp/sourcegit-src rev-parse HEAD` in THIRD-PARTY.md.

---

## File Structure

**Create (vendored, in `src/Styloagent.Git/Vendored/`):**
- `Vendored/Models/Commit.cs` — vendored `Commit` (SHA, Parents, IsMerged, Color, + minimal fields), stripped of `CommitFullMessage`/`Inlines`/app presentation.
- `Vendored/Models/User.cs`, `Vendored/Models/Decorator.cs` — Commit's small deps.
- `Vendored/Models/CommitGraph.cs` — the graph layout (`Generate`, `SetPens`, `SetDefaultPens`, `CommitGraphLayout`, highlighting).
- `Vendored/Controls/CommitGraphControl.cs` — the `Control` that draws the graph (renamed from `CommitGraph` to avoid clashing with the model type).

**Create (ours):**
- `src/Styloagent.Git/THIRD-PARTY.md` — attribution.
- `src/Styloagent.Git/CommitLogParser.cs` — parses `git log` NUL output → `List<Vendored.Models.Commit>`.
- `src/Styloagent.App/ViewModels/GitGraphViewModel.cs` — loads commits, builds the graph.
- `src/Styloagent.App/Views/GitGraphView.axaml` (+`.axaml.cs`) — hosts the vendored control.
- Tests: `tests/Styloagent.Git.Tests/CommitLogParserTests.cs`, `tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs`, `tests/Styloagent.UITests/GitGraphViewTests.cs`.

**Modify:**
- `src/Styloagent.Git/Styloagent.Git.csproj` — add Avalonia package reference(s).
- `src/Styloagent.Core/Git/IGitService.cs` — add `GetCommitsAsync`.
- `src/Styloagent.Git/GitService.cs` — implement `GetCommitsAsync` (uses `CommitLogParser`).
- `src/Styloagent.App/Views/MainWindow.axaml` — add the Git tab.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — expose the selected agent's `GitGraph`.
- Both fake `IGitService` in tests — add `GetCommitsAsync`.

---

## Task 1: Vendor the Commit model closure + THIRD-PARTY.md

**Files:**
- Create: `src/Styloagent.Git/Vendored/Models/Commit.cs`, `User.cs`, `Decorator.cs`, `src/Styloagent.Git/THIRD-PARTY.md`
- Modify: `src/Styloagent.Git/Styloagent.Git.csproj` (add Avalonia reference)
- Test: `tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs` (a placeholder Commit-construction test here; graph test added Task 2)

**Interfaces:**
- Produces: `Styloagent.Git.Vendored.Models.Commit` with public `string SHA`, `List<string> Parents`, `bool IsMerged`, `int Color`, `ulong CommitterTime`, `string Subject`, `User Author`/`Committer`, `List<Decorator> Decorators`, and `void ParseParents(string)`. `User` (Name/Email), `Decorator` (Type enum + Name).

- [ ] **Step 1: Add Avalonia to `Styloagent.Git.csproj`**

Read `src/Styloagent.App/Styloagent.App.csproj` for the exact `Avalonia` package version line (11.3.12). Add to `src/Styloagent.Git/Styloagent.Git.csproj` an ItemGroup with `<PackageReference Include="Avalonia" Version="11.3.12" />` (the vendored model uses `Avalonia.Media`). Do NOT add `Avalonia.Desktop`/`Avalonia.Themes.*` — only the core `Avalonia` package.

- [ ] **Step 2: Copy + adapt the three model files**

Copy `/tmp/sourcegit-src/src/Models/Commit.cs`, `User.cs`, `Decorator.cs` into `src/Styloagent.Git/Vendored/Models/`. In each:
- Rewrite `namespace SourceGit.Models` → `namespace Styloagent.Git.Vendored.Models`.
- Prepend the vendored header comment (see Global Constraints).
- In `Commit.cs`, DELETE the app-coupled presentation members: the `CommitFullMessage` class, the `Inlines`/`InlineElementCollector` property, `IsCommitterVisible`, `IsCurrentHead`, `HasDecorators`, `GetFriendlyName`, and any member referencing `Models.InlineElementCollector` or app resources. KEEP: `SHA`, `Author`, `AuthorTime`, `Committer`, `CommitterTime`, `Subject`, `Parents`, `Decorators`, `IsMerged`, `Color`, `LeftMargin`, `IsHighlightedInGraph`, `FirstParentToCompare`, `ParseParents`, `ParseDecorators`. If `ParseDecorators` references a deleted type, keep the `Decorators` list but simplify `ParseDecorators` to populate `Decorator` records from the `%D` ref string (or drop it if unused by the graph — the graph only needs SHA/Parents/IsMerged/Color).
- Fix any resulting compile errors by removing the offending member (the graph needs none of them).

- [ ] **Step 3: Write the failing test** — create `tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs`:

```csharp
using Styloagent.Git.Vendored.Models;
using Xunit;

public class CommitGraphGenerateTests
{
    [Fact]
    public void Commit_parses_parents_from_space_separated_shas()
    {
        var c = new Commit { SHA = "aaa" };
        c.ParseParents("bbb ccc");
        Assert.Equal(2, c.Parents.Count);
        Assert.Contains("bbb", c.Parents);
    }
}
```

- [ ] **Step 4: Run test to verify it fails then passes**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~CommitGraphGenerateTests"`
Expected: FAIL (types missing) before Step 2 done; PASS after. Also `dotnet build src/Styloagent.Git/Styloagent.Git.csproj -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Write THIRD-PARTY.md + commit**

Create `src/Styloagent.Git/THIRD-PARTY.md` containing: the heading "Third-party code", a line naming SourceGit + its repo URL + MIT, the vendored commit SHA (`git -C /tmp/sourcegit-src rev-parse HEAD`), the list of vendored files, and the full MIT licence text (copy `/tmp/sourcegit-src/LICENSE`).

```bash
git add src/Styloagent.Git/Vendored/Models/ src/Styloagent.Git/THIRD-PARTY.md src/Styloagent.Git/Styloagent.Git.csproj tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): vendor SourceGit Commit model closure (MIT) for the graph

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Vendor the CommitGraph layout model

**Files:**
- Create: `src/Styloagent.Git/Vendored/Models/CommitGraph.cs`
- Test: `tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs` (extend)

**Interfaces:**
- Consumes: `Vendored.Models.Commit` (Task 1).
- Produces: `Styloagent.Git.Vendored.Models.CommitGraph` with
  `static CommitGraph Generate(List<Commit> commits, bool recalculateMergeState, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits)`,
  `static void SetDefaultPens(double thickness = 2)`, `static void SetPens(List<Color> colors, double thickness)`,
  and `record CommitGraphLayout(double StartY, double ClipWidth, double RowHeight)`.

- [ ] **Step 1: Copy + adapt** — copy `/tmp/sourcegit-src/src/Models/CommitGraph.cs` to `src/Styloagent.Git/Vendored/Models/CommitGraph.cs`. Rewrite namespace to `Styloagent.Git.Vendored.Models`, add the vendored header. It uses `System`, `Avalonia`, `Avalonia.Media`, and the vendored `Commit` — all available. Remove any member that references SourceGit app types (the graph is self-contained; expect none beyond `Commit`). If `SetPens` defaults reference app colors, keep the signature and let the caller supply colors.

- [ ] **Step 2: Write the failing test** — add to `CommitGraphGenerateTests.cs`:

```csharp
    [Fact]
    public void Generate_builds_a_graph_from_a_linear_history()
    {
        CommitGraph.SetDefaultPens();
        var commits = new List<Commit>
        {
            new() { SHA = "c", Parents = { "b" }, Color = 0 },
            new() { SHA = "b", Parents = { "a" }, Color = 0 },
            new() { SHA = "a", Color = 0 },
        };
        var graph = CommitGraph.Generate(commits, recalculateMergeState: false,
            firstParentOnlyEnabled: false, CommitGraphHighlighting.All, new HashSet<string>());
        Assert.NotNull(graph);
    }
```
(If `Commit.Parents` is not settable via collection-initializer, construct the list explicitly and call `ParseParents`.)

- [ ] **Step 3: Run + commit**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~CommitGraphGenerateTests"` → all pass. `dotnet build src/Styloagent.Git -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Git/Vendored/Models/CommitGraph.cs tests/Styloagent.Git.Tests/CommitGraphGenerateTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): vendor SourceGit CommitGraph layout model (MIT)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `git log` → Commit parser + `IGitService.GetCommitsAsync`

**Files:**
- Create: `src/Styloagent.Git/CommitLogParser.cs`
- Modify: `src/Styloagent.Core/Git/IGitService.cs`, `src/Styloagent.Git/GitService.cs`
- Modify (fakes): both fake `IGitService` in `tests/` (see note)
- Test: `tests/Styloagent.Git.Tests/CommitLogParserTests.cs`

**Interfaces:**
- Produces:
  - `static List<Vendored.Models.Commit> CommitLogParser.Parse(string nulSeparatedLog)` — parses `git log --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s` (fields NUL-separated, records newline-separated).
  - `IGitService.GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default)` → `Task<GitResult<IReadOnlyList<Vendored.Models.Commit>>>`.

- [ ] **Step 1: Write the failing parser test** — create `tests/Styloagent.Git.Tests/CommitLogParserTests.cs`:

```csharp
using Styloagent.Git;
using Xunit;

public class CommitLogParserTests
{
    [Fact]
    public void Parse_reads_sha_parents_and_subject()
    {
        // fields: SHA \0 Parents \0 Decorators \0 author±email \0 at \0 committer±email \0 ct \0 subject
        var log =
            "aaa bbb ccc HEAD -> main Ann±a@x 1700000000 Ann±a@x 1700000000 top\n" +
            "bbb   Ann±a@x 1699999999 Ann±a@x 1699999999 root\n";
        var commits = CommitLogParser.Parse(log);
        Assert.Equal(2, commits.Count);
        Assert.Equal("aaa", commits[0].SHA);
        Assert.Equal(2, commits[0].Parents.Count);
        Assert.Equal("top", commits[0].Subject);
        Assert.Empty(commits[1].Parents);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~CommitLogParserTests"`
Expected: FAIL — `CommitLogParser` missing.

- [ ] **Step 3: Implement the parser** — create `src/Styloagent.Git/CommitLogParser.cs`:

```csharp
using System.Globalization;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.Git;

/// <summary>
/// Parses the NUL-delimited <c>git log</c> format SourceGit's graph model expects
/// (<c>%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s</c>) into vendored commits.
/// </summary>
public static class CommitLogParser
{
    public static List<Commit> Parse(string nulSeparatedLog)
    {
        var commits = new List<Commit>();
        foreach (var raw in nulSeparatedLog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var f = line.Split(' ');
            if (f.Length < 8) continue;

            var commit = new Commit { SHA = f[0], Subject = f[7] };
            if (!string.IsNullOrEmpty(f[1])) commit.ParseParents(f[1]);
            commit.Author = ParseUser(f[3]);
            commit.Committer = ParseUser(f[5]);
            commit.CommitterTime = ulong.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0;
            commits.Add(commit);
        }
        return commits;
    }

    private static User ParseUser(string field)
    {
        var i = field.IndexOf('±');
        return i < 0 ? new User { Name = field } : new User { Name = field[..i], Email = field[(i + 1)..] };
    }
}
```
(Adjust `User`/`Commit` member names to match the vendored types from Task 1. If `Parents` is read-only, use `ParseParents` as shown.)

- [ ] **Step 4: Add `GetCommitsAsync` to the interface + GitService**

`src/Styloagent.Core/Git/IGitService.cs` — the interface references the vendored `Commit`, so it needs `Styloagent.Git` visible from Core. Because Core must NOT depend on `Styloagent.Git`, DECLARE `GetCommitsAsync` to return commits as the vendored type via a Core-visible shape is not possible without a dependency. RESOLUTION: move the method OFF `IGitService` and instead expose it on a new narrow interface `IGitLog` defined IN `Styloagent.Git` (namespace `Styloagent.Git`), implemented by `GitService`:

```csharp
// src/Styloagent.Git/IGitLog.cs
using Styloagent.Core.Git;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.Git;

public interface IGitLog
{
    Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default);
}
```
`GitService : IGitService, IGitLog` implements it by running
`log -{limit} --date-order --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s`
in `worktreePath` (reuse the private `RunAsync` with `.ConfigureAwait(false)`), then `CommitLogParser.Parse(stdout)`. On failure return `GitResult<IReadOnlyList<Commit>>.Fail(stderr)`.

This keeps `Styloagent.Core` free of the vendored types (the App references `Styloagent.Git` already, so it can consume `IGitLog`). Update NO existing `IGitService` fakes (the method is on the new `IGitLog`, which fakes implement only where needed — Task 5's VM test).

- [ ] **Step 5: Run + commit**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~CommitLogParserTests"` → PASS. `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Git/CommitLogParser.cs src/Styloagent.Git/IGitLog.cs src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/CommitLogParserTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): git-log parser + IGitLog.GetCommitsAsync feeding the vendored graph

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Vendor the CommitGraph rendering control

**Files:**
- Create: `src/Styloagent.Git/Vendored/Controls/CommitGraphControl.cs`
- Test: none (rendering is exercised by the headless test in Task 6)

**Interfaces:**
- Consumes: `Vendored.Models.CommitGraph`, `Vendored.Models.CommitGraphLayout` (Tasks 1-2).
- Produces: `Styloagent.Git.Vendored.Controls.CommitGraphControl : Avalonia.Controls.Control` with `Graph` (DirectProperty of `CommitGraph`), `Layout` (DirectProperty of `CommitGraphLayout`), `DotBrush` (StyledProperty of `IBrush`), and the SourceGit `Render` override.

- [ ] **Step 1: Copy + adapt** — copy `/tmp/sourcegit-src/src/Views/CommitGraph.cs` to `src/Styloagent.Git/Vendored/Controls/CommitGraphControl.cs`. Then:
  - Rewrite `namespace SourceGit.Views` → `namespace Styloagent.Git.Vendored.Controls`; add the vendored header.
  - Rename the class `CommitGraph` → `CommitGraphControl` (avoids clashing with the model type `Vendored.Models.CommitGraph`). Update the `DirectProperty<CommitGraph, …>` self-references to `DirectProperty<CommitGraphControl, …>`.
  - Its type references `Models.CommitGraph`/`Models.CommitGraphLayout` become `Styloagent.Git.Vendored.Models.CommitGraph`/`CommitGraphLayout` (add `using Styloagent.Git.Vendored.Models;`).
  - It uses only `Avalonia`, `Avalonia.Controls`, `Avalonia.Media` — all present. Remove any reference to SourceGit `App`/resources if present (the control exposes `DotBrush` as a StyledProperty, so colors come from the caller — expect no app coupling).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Styloagent.Git/Styloagent.Git.csproj -clp:ErrorsOnly`
Expected: 0 errors. (If the control references a SourceGit member not vendored, remove/adapt that member and note it in the report.)

- [ ] **Step 3: Commit**

```bash
git add src/Styloagent.Git/Vendored/Controls/CommitGraphControl.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): vendor SourceGit commit-graph rendering control (MIT)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `GitGraphViewModel` (loads commits, builds the graph)

**Files:**
- Create: `src/Styloagent.App/ViewModels/GitGraphViewModel.cs`
- Test: `tests/Styloagent.App.Tests/GitGraphViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitLog` (Task 3), `Vendored.Models.CommitGraph` (Task 2).
- Produces: `GitGraphViewModel(IGitLog log)` with `Task LoadAsync(string worktreePath)` that sets an observable `CommitGraph? Graph` (the built `Vendored.Models.CommitGraph`) and `int CommitCount`; empty/failure → `Graph = null`, `CommitCount = 0`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.App.Tests/GitGraphViewModelTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

public class GitGraphViewModelTests
{
    private sealed class FakeLog : IGitLog
    {
        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default)
        {
            IReadOnlyList<Commit> commits = new List<Commit>
            {
                new() { SHA = "b", Color = 0 },
                new() { SHA = "a", Color = 0 },
            };
            return Task.FromResult(GitResult<IReadOnlyList<Commit>>.Success(commits));
        }
    }

    [Fact]
    public async Task LoadAsync_builds_a_graph_and_counts_commits()
    {
        var vm = new GitGraphViewModel(new FakeLog());
        await vm.LoadAsync("/repo/.worktrees/foss");
        Assert.NotNull(vm.Graph);
        Assert.Equal(2, vm.CommitCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~GitGraphViewModelTests"`
Expected: FAIL — `GitGraphViewModel` missing.

- [ ] **Step 3: Implement** — create `src/Styloagent.App/ViewModels/GitGraphViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Loads a worktree's recent history via <see cref="IGitLog"/> and builds the vendored
/// <see cref="CommitGraph"/> the graph control renders. Read-only (Plan 2a).
/// </summary>
public sealed partial class GitGraphViewModel : ObservableObject
{
    private readonly IGitLog _log;

    [ObservableProperty]
    private CommitGraph? _graph;

    [ObservableProperty]
    private int _commitCount;

    public GitGraphViewModel(IGitLog log) => _log = log;

    public async Task LoadAsync(string worktreePath)
    {
        var result = await _log.GetCommitsAsync(worktreePath);
        if (!result.Ok || result.Value is null || result.Value.Count == 0)
        {
            Graph = null;
            CommitCount = 0;
            return;
        }
        var commits = new List<Commit>(result.Value);
        CommitGraph.SetDefaultPens();
        Graph = CommitGraph.Generate(commits, recalculateMergeState: false,
            firstParentOnlyEnabled: false, CommitGraphHighlighting.All, new HashSet<string>());
        CommitCount = commits.Count;
    }
}
```

- [ ] **Step 4: Run + commit**

Run: the focused test → PASS; then full `Styloagent.App.Tests` → all green. `dotnet build src/Styloagent.App -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.App/ViewModels/GitGraphViewModel.cs tests/Styloagent.App.Tests/GitGraphViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): GitGraphViewModel builds the vendored graph from IGitLog

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `GitGraphView` + headless render test

**Files:**
- Create: `src/Styloagent.App/Views/GitGraphView.axaml`, `GitGraphView.axaml.cs`
- Test: `tests/Styloagent.UITests/GitGraphViewTests.cs`

**Interfaces:**
- Consumes: `GitGraphViewModel` (Task 5), `CommitGraphControl` (Task 4).

- [ ] **Step 1: Create the view** — `src/Styloagent.App/Views/GitGraphView.axaml` hosts the vendored control bound to the VM's `Graph`, with an empty-state and a commit count. Reference the vendored control's CLR namespace:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Styloagent.App.ViewModels"
             xmlns:graph="clr-namespace:Styloagent.Git.Vendored.Controls;assembly=Styloagent.Git"
             x:Class="Styloagent.App.Views.GitGraphView"
             x:DataType="vm:GitGraphViewModel">
  <Border Background="{DynamicResource PanelBgBrush}" CornerRadius="4">
    <Grid RowDefinitions="Auto,*">
      <Border Grid.Row="0" Background="{DynamicResource PanelHeaderBgBrush}" Padding="8,6">
        <TextBlock Text="{Binding CommitCount, StringFormat='History · {0} commits'}"
                   FontSize="11" Foreground="{DynamicResource MutedTextBrush}" />
      </Border>
      <TextBlock Grid.Row="1" IsVisible="{Binding Graph, Converter={x:Static ObjectConverters.IsNull}}"
                 Text="No history — this agent has no worktree yet."
                 FontSize="11" Foreground="{DynamicResource MutedTextBrush}" Padding="12" />
      <ScrollViewer Grid.Row="1" IsVisible="{Binding Graph, Converter={x:Static ObjectConverters.IsNotNull}}">
        <graph:CommitGraphControl Graph="{Binding Graph}" DotBrush="{DynamicResource AccentBrush}" />
      </ScrollViewer>
    </Grid>
  </Border>
</UserControl>
```
`GitGraphView.axaml.cs` is the standard `InitializeComponent()` partial (mirror `IssuesView.axaml.cs`).

> If `CommitGraphControl` requires a non-null `Layout` to render, set a sensible default `CommitGraphLayout` in the VM (`new CommitGraphLayout(0, 400, 24)`) and bind it too; the implementer adds a `Layout` property to the VM if the control needs it.

- [ ] **Step 2: Write the headless render test** — create `tests/Styloagent.UITests/GitGraphViewTests.cs` mirroring `IssuesViewTests`: build a `GitGraphViewModel` with a fake `IGitLog` returning a few commits, `await vm.LoadAsync(...)`, host `GitGraphView` in a `Window`, `HeadlessRender.SettleAsync`, assert the header shows the count, and `ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-gitgraph.png")`.

- [ ] **Step 3: Run + commit**

Run: `dotnet test tests/Styloagent.UITests/Styloagent.UITests.csproj --filter "FullyQualifiedName~GitGraphViewTests"` → PASS (screenshot written). Inspect `/tmp/styloagent-gitgraph.png` shows lanes/dots.

```bash
git add src/Styloagent.App/Views/GitGraphView.axaml src/Styloagent.App/Views/GitGraphView.axaml.cs tests/Styloagent.UITests/GitGraphViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): GitGraphView hosts the vendored commit-graph control

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Git tab in the shell (selected agent's worktree graph)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` (expose `GitGraph`; refresh on selection/spawn), `src/Styloagent.App/Views/MainWindow.axaml` (Git tab), `src/Styloagent.App/App.axaml.cs` (inject the `IGitLog` — the existing `GitService` already implements it)

**Interfaces:**
- Consumes: `GitGraphViewModel` (Task 5), `IGitLog` (Task 3), `AgentPaneViewModel.WorktreePath` (Plan 1).

- [ ] **Step 1: Expose `GitGraph` on the VM** — add `[ObservableProperty] private GitGraphViewModel? _gitGraph;`. Construct it once the `IGitLog` is available (the VM already holds `_git` as `IGitService`; since `GitService` implements both, pass the same instance as `IGitLog` — add an `IGitLog? _gitLog` field set in `InitializeAsync` alongside `_git`, defaulting null; in `App.axaml.cs` the single `new Styloagent.Git.GitService()` is passed for both `gitService:` and a new `gitLog:` parameter). When `SelectedPane` changes to a pane with a non-null `WorktreePath`, call `_ = GitGraph.LoadAsync(pane.WorktreePath)` (fire-and-forget, mirrors the badge refresh). Create `GitGraph = new GitGraphViewModel(_gitLog)` when `_gitLog` is set.

- [ ] **Step 2: Add the Git tab** — in `MainWindow.axaml`, add a `TabItem` (icon `BranchFork` or `Flowchart`) after the Issues tab, hosting `<views:GitGraphView DataContext="{Binding GitGraph}" />`.

- [ ] **Step 3: Build + run the App suite**

Run: `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors; `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj` → all green.

- [ ] **Step 4: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/Views/MainWindow.axaml src/Styloagent.App/App.axaml.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): Git tab shows the selected agent's worktree history graph

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Full-suite green + solution build

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** run Core, App, Git, UITests suites (`--no-build` where possible) → all pass (Git integration + graph tests included; UITests includes the new GitGraph render).
- [ ] **Step 3:** Inspect `/tmp/styloagent-gitgraph.png` — the graph renders lanes/dots for the fixture history.
- [ ] **Step 4:** Commit any incidental fixes with the standard trailer.

---

## Self-Review

**Spec coverage (Plan 2a slice of the git-client spec):**
- Reuse SourceGit commit-graph control (MIT + attribution) → Tasks 1-2, 4 + THIRD-PARTY.md (Task 1). ✓
- Feed the graph from our git backend → Task 3 (`CommitLogParser` + `IGitLog.GetCommitsAsync`). ✓
- Per-agent history graph in a Git panel/tab → Tasks 5-7. ✓
- Vendored code isolated in `Styloagent.Git`, App-shell coupling avoided → vendored files strip App/Preferences/locale members (Tasks 1, 4). ✓
- **Deferred to 2b/2c (documented, not gaps):** AvaloniaEdit diff view + changes list (2b); stage/commit/push/pull/branch/stash + full panel layout + roster git badges wired to the graph (2c); the Avalonia 11.3.18 bump (2b, only if AvaloniaEdit needs it).

**Deviation from the spec (intentional, noted):** `GetCommitsAsync` is on a new `IGitLog` interface in `Styloagent.Git` rather than on `Styloagent.Core.Git.IGitService`, because it returns the vendored `Commit` type and `Styloagent.Core` must not depend on `Styloagent.Git`. The App consumes `IGitLog` directly (it already references `Styloagent.Git`).

**Placeholder scan:** vendoring tasks specify exact source files, the exact namespace rewrite, and the exact members to strip — concrete, not "adapt as needed." The one soft spot (does `CommitGraphControl` need a bound `Layout`) is called out in Task 6 with the implementer instruction to add it if the control requires it.

**Type consistency:** `Vendored.Models.Commit`/`CommitGraph`/`CommitGraphLayout`, `IGitLog.GetCommitsAsync`, and `GitGraphViewModel.Graph` (type `CommitGraph`) are consistent across Tasks 2-7. `CommitGraphControl` (renamed from SourceGit's `CommitGraph`) is used consistently in Tasks 4, 6.
