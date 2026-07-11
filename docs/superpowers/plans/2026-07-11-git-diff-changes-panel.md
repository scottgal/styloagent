# Diff + Changes Panel (Plan 2b of the git client) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the human see what an agent changed — a changes list (from the worktree's git status) and a lightweight, readable unified diff per file — inside the Git tab, with NO AvaloniaEdit/forked-editor dependency and NO Avalonia bump.

**Architecture:** Author our own small diff data types (`DiffLine`/`DiffLineKind`/`FileDiff`) and a `UnifiedDiffParser` whose line-classification mirrors SourceGit's proven approach (hunk `@@` → indicator; `+`/`-`/` ` → added/deleted/context). A new `IGitDiff.GetDiffAsync` on `Styloagent.Git` runs `git diff` and parses it. The changes list reuses Plan 1's existing `GitStatus.Changes`. A custom `DiffView` renders coloured monospace line rows with an old/new line-number gutter. Everything binds into the Git tab beside the commit graph (Plan 2a).

**Tech Stack:** .NET 10, Avalonia 11.3.12 (NO bump), CommunityToolkit.Mvvm, xUnit. Git CLI.

## Global Constraints

- net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build clean (0 warnings/0 errors; pre-existing NU1903 Tmds.DBus warnings are not ours).
- **No AvaloniaEdit, no forked editor, no Avalonia bump.** The diff control is plain Avalonia (`ItemsControl` of line rows, monospace `TextBlock`s, colour by kind).
- The diff parser's line-classification logic is DERIVED FROM SourceGit's `Commands/Diff.cs` (MIT). The `UnifiedDiffParser` file carries a comment: `// Unified-diff line classification derived from SourceGit (MIT). See Styloagent.Git/THIRD-PARTY.md`. Add a "Derived logic" note to `THIRD-PARTY.md` (no files are copied verbatim, so no new vendored-file entries — a derivation note suffices).
- Reuse Plan 1's `GitStatus`/`GitChange`/`GitChangeKind` (from `Styloagent.Core.Git`) for the changes list — do NOT re-implement status parsing.
- ConfigureAwait: any new `await` in `GitService` that may be blocked-on from the UI thread uses `.ConfigureAwait(false)` (reuse the existing `RunAsync`).
- Commit directly to `main` (no new branch), authored `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` + trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Create:**
- `src/Styloagent.Core/Git/DiffModel.cs` — `DiffLineKind` enum, `DiffLine` record, `FileDiff` record (UI-free; the diff data shape).
- `src/Styloagent.Git/UnifiedDiffParser.cs` — `Parse(string gitDiffText)` → `FileDiff`.
- `src/Styloagent.Git/IGitDiff.cs` — `GetDiffAsync(worktreePath, relativePath, staged)` seam.
- `src/Styloagent.App/ViewModels/DiffViewModel.cs` — holds a `FileDiff` + display rows.
- `src/Styloagent.App/ViewModels/ChangesViewModel.cs` — the worktree's changed files (from `GitStatus`) + selection → loads a diff.
- `src/Styloagent.App/Views/DiffView.axaml` (+`.axaml.cs`) — the line-row diff control.
- `src/Styloagent.App/Views/ChangesView.axaml` (+`.axaml.cs`) — the changed-files list + hosted `DiffView`.
- `src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs` — line kind → row background/foreground brush.
- Tests: `tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs`, `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`, `tests/Styloagent.UITests/DiffViewTests.cs`.

**Modify:**
- `src/Styloagent.Git/GitService.cs` — implement `IGitDiff` (add `GitService : …, IGitDiff`).
- `src/Styloagent.Git/THIRD-PARTY.md` — add the "Derived logic" note.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — expose `Changes` (a `ChangesViewModel`), load on SelectedPane change (like `GitGraph`).
- `src/Styloagent.App/Views/GitGraphView.axaml` OR `MainWindow.axaml` — place the changes/diff panel in the Git tab beside the graph (see Task 7).
- `src/Styloagent.App/App.axaml.cs` — pass the shared `GitService` as the `IGitDiff` too.

---

## Task 1: Diff data model

**Files:**
- Create: `src/Styloagent.Core/Git/DiffModel.cs`
- Test: `tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs` (placeholder construction test here; parser added Task 2)

**Interfaces:**
- Produces:
  - `enum DiffLineKind { Header, Added, Deleted, Context }`
  - `record DiffLine(DiffLineKind Kind, string Content, int OldLine, int NewLine)`
  - `record FileDiff(string Path, int Added, int Deleted, bool IsBinary, IReadOnlyList<DiffLine> Lines)` with `static FileDiff Empty(string path) => new(path, 0, 0, false, System.Array.Empty<DiffLine>());`

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs`:

```csharp
using Styloagent.Core.Git;
using Xunit;

public class UnifiedDiffParserTests
{
    [Fact]
    public void FileDiff_empty_has_no_lines()
    {
        var d = FileDiff.Empty("src/Foo.cs");
        Assert.Equal("src/Foo.cs", d.Path);
        Assert.Empty(d.Lines);
        Assert.False(d.IsBinary);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~UnifiedDiffParserTests"` → FAIL (types missing).

- [ ] **Step 3: Implement** — create `src/Styloagent.Core/Git/DiffModel.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>The role of a line in a unified diff.</summary>
public enum DiffLineKind { Header, Added, Deleted, Context }

/// <summary>One line of a unified diff, with its old/new line numbers (0 when N/A).</summary>
public sealed record DiffLine(DiffLineKind Kind, string Content, int OldLine, int NewLine);

/// <summary>A parsed unified diff for a single file.</summary>
public sealed record FileDiff(string Path, int Added, int Deleted, bool IsBinary, IReadOnlyList<DiffLine> Lines)
{
    public static FileDiff Empty(string path) => new(path, 0, 0, false, System.Array.Empty<DiffLine>());
}
```

- [ ] **Step 4: Run to verify it passes** — the focused test → PASS; `dotnet build src/Styloagent.Core -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/DiffModel.cs tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): diff data model (DiffLine/FileDiff)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `UnifiedDiffParser`

**Files:**
- Create: `src/Styloagent.Git/UnifiedDiffParser.cs`
- Modify: `src/Styloagent.Git/THIRD-PARTY.md` (add the "Derived logic" note)
- Test: `tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs` (extend)

**Interfaces:**
- Consumes: `DiffModel` (Task 1).
- Produces: `static FileDiff UnifiedDiffParser.Parse(string path, string gitDiffText)` — classifies unified-diff lines: hunk header (`@@ -a,b +c,d @@`) → `Header` (resets old/new counters); `+` → `Added` (newLine++); `-` → `Deleted` (oldLine++); ` ` (space) → `Context` (both++); lines before the first hunk (`diff --git`, `index`, `+++`, `---`) are ignored; a `Binary files … differ` line sets `IsBinary`.

- [ ] **Step 1: Write the failing test** — add to `UnifiedDiffParserTests.cs`:

```csharp
    private const string Sample =
        "diff --git a/Foo.cs b/Foo.cs\n" +
        "index 111..222 100644\n" +
        "--- a/Foo.cs\n" +
        "+++ b/Foo.cs\n" +
        "@@ -1,3 +1,3 @@\n" +
        " unchanged\n" +
        "-old line\n" +
        "+new line\n" +
        " tail\n";

    [Fact]
    public void Parse_classifies_added_deleted_and_context()
    {
        var d = UnifiedDiffParser.Parse("Foo.cs", Sample);
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Deleted);
        Assert.False(d.IsBinary);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Added && l.Content == "new line" && l.NewLine == 2);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Deleted && l.Content == "old line" && l.OldLine == 2);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Header);
        Assert.Contains(d.Lines, l => l.Kind == DiffLineKind.Context && l.Content == "unchanged");
    }

    [Fact]
    public void Parse_flags_binary()
    {
        var d = UnifiedDiffParser.Parse("img.png", "diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n");
        Assert.True(d.IsBinary);
    }
```

- [ ] **Step 2: Run to verify it fails** — focused test → FAIL (`UnifiedDiffParser` missing).

- [ ] **Step 3: Implement** — create `src/Styloagent.Git/UnifiedDiffParser.cs`:

```csharp
using System.Text.RegularExpressions;
using Styloagent.Core.Git;

namespace Styloagent.Git;

// Unified-diff line classification derived from SourceGit (MIT). See Styloagent.Git/THIRD-PARTY.md
/// <summary>Parses <c>git diff</c> unified output for one file into a <see cref="FileDiff"/>.</summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static FileDiff Parse(string path, string gitDiffText)
    {
        var lines = new List<DiffLine>();
        int added = 0, deleted = 0, oldLine = 0, newLine = 0;
        bool inHunk = false, isBinary = false;

        foreach (var raw in gitDiffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inHunk)
            {
                if (line.StartsWith("Binary files ", System.StringComparison.Ordinal)) { isBinary = true; continue; }
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    oldLine = int.Parse(m.Groups[1].Value);
                    newLine = int.Parse(m.Groups[2].Value);
                    lines.Add(new DiffLine(DiffLineKind.Header, line, 0, 0));
                    inHunk = true;
                }
                continue; // skip diff --git / index / --- / +++ preamble
            }

            var next = HunkHeader().Match(line);
            if (next.Success)
            {
                oldLine = int.Parse(next.Groups[1].Value);
                newLine = int.Parse(next.Groups[2].Value);
                lines.Add(new DiffLine(DiffLineKind.Header, line, 0, 0));
                continue;
            }
            if (line.Length == 0) continue;

            var prefix = line[0];
            var content = line[1..];
            switch (prefix)
            {
                case '-':
                    added += 0; deleted++;
                    lines.Add(new DiffLine(DiffLineKind.Deleted, content, oldLine, 0));
                    oldLine++;
                    break;
                case '+':
                    added++;
                    lines.Add(new DiffLine(DiffLineKind.Added, content, 0, newLine));
                    newLine++;
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, content, oldLine, newLine));
                    oldLine++; newLine++;
                    break;
                case '\\': // "\ No newline at end of file" — ignore
                    break;
                default:
                    break;
            }
        }

        return new FileDiff(path, added, deleted, isBinary, lines);
    }
}
```

- [ ] **Step 4: Run to verify it passes** — focused test → PASS (both tests). `dotnet build src/Styloagent.Git -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: THIRD-PARTY.md note + commit** — append to `src/Styloagent.Git/THIRD-PARTY.md` under a new `### Derived logic` heading:
`- \`UnifiedDiffParser.cs\` — unified-diff line classification derived from SourceGit's \`src/Commands/Diff.cs\` (no verbatim copy).`

```bash
git add src/Styloagent.Git/UnifiedDiffParser.cs src/Styloagent.Git/THIRD-PARTY.md tests/Styloagent.Git.Tests/UnifiedDiffParserTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): unified-diff parser (line classification derived from SourceGit)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `IGitDiff.GetDiffAsync` on GitService

**Files:**
- Create: `src/Styloagent.Git/IGitDiff.cs`
- Modify: `src/Styloagent.Git/GitService.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs` (extend — opt-in, skips without git)

**Interfaces:**
- Consumes: `UnifiedDiffParser` (Task 2), `FileDiff` (Task 1).
- Produces: `interface IGitDiff { Task<GitResult<FileDiff>> GetDiffAsync(string worktreePath, string relativePath, bool staged, CancellationToken ct = default); }` (namespace `Styloagent.Git`); `GitService : …, IGitDiff`.

- [ ] **Step 1: Create `IGitDiff.cs`:**

```csharp
using Styloagent.Core.Git;

namespace Styloagent.Git;

public interface IGitDiff
{
    Task<GitResult<FileDiff>> GetDiffAsync(string worktreePath, string relativePath, bool staged, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing integration test** — add to `GitServiceIntegrationTests.cs` (mirrors the existing skip-without-git pattern): init a temp repo, commit a file, modify it, `GetDiffAsync(repo, "a.txt", staged: false)`, assert `Ok`, `Added>=1` or a line whose Content matches the new text.

```csharp
    [Fact]
    public async Task GetDiff_reports_an_unstaged_change()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitdiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            var result = await git.GetDiffAsync(repo, "a.txt", staged: false);
            Assert.True(result.Ok, result.Error);
            Assert.Contains(result.Value!.Lines, l => l.Content == "two");
        }
        finally { TryDeleteRepo(repo); }
    }
```

- [ ] **Step 3: Run to verify it fails** — `dotnet test tests/Styloagent.Git.Tests/... --filter "FullyQualifiedName~GetDiff_reports"` → FAIL.

- [ ] **Step 4: Implement in GitService** — add `IGitDiff` to the class declaration and the method (reuse the private `RunAsync`, which already uses `.ConfigureAwait(false)`):

```csharp
    public async Task<GitResult<FileDiff>> GetDiffAsync(string worktreePath, string relativePath, bool staged, CancellationToken ct = default)
    {
        var args = staged
            ? new[] { "diff", "--staged", "--no-color", "--", relativePath }
            : new[] { "diff", "--no-color", "--", relativePath };
        var r = await RunAsync(worktreePath, ct, args).ConfigureAwait(false);
        return r.Ok
            ? GitResult<FileDiff>.Success(UnifiedDiffParser.Parse(relativePath, r.Stdout))
            : GitResult<FileDiff>.Fail(r.Stderr);
    }
```
(If `RunAsync`'s signature is `RunAsync(dir, ct, params string[])`, pass the args via `params`; adapt to the real signature you read in GitService.cs.)

- [ ] **Step 5: Run + commit** — focused integration test → PASS (or skips without git); `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Git/IGitDiff.cs src/Styloagent.Git/GitService.cs tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): IGitDiff.GetDiffAsync runs git diff and parses it

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `ChangesViewModel` (changed files → diff)

**Files:**
- Create: `src/Styloagent.App/ViewModels/ChangesViewModel.cs`, `src/Styloagent.App/ViewModels/DiffViewModel.cs`
- Test: `tests/Styloagent.App.Tests/ChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitService` (Plan 1, for `GetStatusAsync` → `GitStatus.Changes`), `IGitDiff` (Task 3), `FileDiff`/`GitChange`.
- Produces:
  - `DiffViewModel` — `[ObservableProperty] FileDiff? File;` + `bool HasDiff => File is { Lines.Count: > 0 };`
  - `ChangesViewModel(IGitService git, IGitDiff diff)` with `ObservableCollection<GitChange> Files`, `[ObservableProperty] GitChange? SelectedFile`, `DiffViewModel Diff { get; }`, `Task LoadAsync(string worktreePath)` (fills `Files` from status), and `OnSelectedFileChanged` → loads that file's diff into `Diff`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.App.Tests/ChangesViewModelTests.cs` with a fake `IGitService` (returns a `GitStatus` with two `GitChange`s) and a fake `IGitDiff` (returns a `FileDiff` with one added line). Assert `LoadAsync` fills `Files` (count 2), and setting `SelectedFile` loads `Diff.File` with the expected line.

```csharp
using System.Collections.Generic;
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

public class ChangesViewModelTests
{
    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 0, 0, false,
                new[] { new GitChange("a.txt", GitChangeKind.Modified), new GitChange("b.txt", GitChangeKind.Added) })));
        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeDiff : IGitDiff
    {
        public Task<GitResult<FileDiff>> GetDiffAsync(string w, string path, bool staged, CancellationToken ct = default)
            => Task.FromResult(GitResult<FileDiff>.Success(new FileDiff(path, 1, 0, false,
                new[] { new DiffLine(DiffLineKind.Added, "hello", 0, 1) })));
    }

    [Fact]
    public async Task Load_lists_files_and_selecting_one_loads_its_diff()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff());
        await vm.LoadAsync("/wt");
        Assert.Equal(2, vm.Files.Count);

        vm.SelectedFile = vm.Files[0];
        await vm.WaitForDiffAsync();      // see note
        Assert.NotNull(vm.Diff.File);
        Assert.Contains(vm.Diff.File!.Lines, l => l.Content == "hello");
    }
}
```

> Note: if `OnSelectedFileChanged` fires the diff load as fire-and-forget, expose a small `Task WaitForDiffAsync()` (returns the in-flight load task, or `Task.CompletedTask`) so the test can await it deterministically. Alternatively make `SelectFileAsync(GitChange)` an awaitable method the test calls directly and the view binds via a command. Choose the awaitable-method approach if fire-and-forget makes the test flaky.

- [ ] **Step 2: Run to verify it fails** — focused test → FAIL.

- [ ] **Step 3: Implement `DiffViewModel.cs` + `ChangesViewModel.cs`** (fill `Files` from `GetStatusAsync().Value.Changes`; on select, `GetDiffAsync(worktreePath, file.Path, staged:false)` → `Diff.File`). Use an awaitable `SelectFileAsync` (bound by the view) to keep the test deterministic; store the last `worktreePath` from `LoadAsync`.

- [ ] **Step 4: Run + commit** — focused test → PASS; full App.Tests → green.

```bash
git add src/Styloagent.App/ViewModels/ChangesViewModel.cs src/Styloagent.App/ViewModels/DiffViewModel.cs tests/Styloagent.App.Tests/ChangesViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): ChangesViewModel lists changed files and loads per-file diffs

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `DiffView` control + line-kind brush converter

**Files:**
- Create: `src/Styloagent.App/Views/DiffView.axaml` (+`.axaml.cs`), `src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs`
- Test: `tests/Styloagent.UITests/DiffViewTests.cs`

**Interfaces:**
- Consumes: `DiffViewModel` (Task 4), `DiffLine`/`DiffLineKind`.
- Produces: a monospace line-row diff view; `DiffLineKindBrushConverter` maps `DiffLineKind` → row background (Added=#1E3A24 green-ish, Deleted=#3A1E1E red-ish, Header=#2A2A3A, Context=transparent) with a `parameter` to also yield foregrounds if needed.

- [ ] **Step 1: Create the converter** — `DiffLineKindBrushConverter : IValueConverter` (mirror `SeverityColorConverter` from the Issues feature): Added/Deleted/Header/Context → `SolidColorBrush`. Provide light+dark-friendly muted colours.

- [ ] **Step 2: Create `DiffView.axaml`** — an `ItemsControl` over `Diff.File.Lines` (bind `DataContext` to a `DiffViewModel`), each row a `Grid` with an old/new line-number gutter (small muted monospace) + the content in a monospace `TextBlock` (`FontFamily="Cascadia Mono, Consolas, monospace"`), row `Background` from `DiffLineKindBrushConverter`. Empty-state ("Select a file to see its diff") when `!HasDiff`. Header rows (`@@ …`) render in the header colour. Mirror `IssuesView.axaml` for theme tokens + code-behind.

- [ ] **Step 3: Write the headless render test** — `tests/Styloagent.UITests/DiffViewTests.cs` mirroring `IssuesViewTests`: build a `DiffViewModel { File = new FileDiff("Foo.cs", 1, 1, false, [header, context, deleted, added]) }`, host `DiffView`, `SettleAsync`, assert a TextBlock shows the added content, screenshot to `/tmp/styloagent-diff.png`.

- [ ] **Step 4: Run + commit** — render test → PASS; inspect `/tmp/styloagent-diff.png` shows red/green rows.

```bash
git add src/Styloagent.App/Views/DiffView.axaml src/Styloagent.App/Views/DiffView.axaml.cs src/Styloagent.App/Converters/DiffLineKindBrushConverter.cs tests/Styloagent.UITests/DiffViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): lightweight DiffView (coloured monospace line rows)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `ChangesView` (files list + hosted diff) + Git-tab placement

**Files:**
- Create: `src/Styloagent.App/Views/ChangesView.axaml` (+`.axaml.cs`)
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`, `src/Styloagent.App/App.axaml.cs`, and the Git-tab content in `MainWindow.axaml`

**Interfaces:**
- Consumes: `ChangesViewModel` (Task 4), `DiffView` (Task 5), `GitGraphView` (Plan 2a).

- [ ] **Step 1: Create `ChangesView.axaml`** — a `Grid` with the changed-files `ListBox` (bound to `Files`, `SelectedItem` → `SelectedFile`, each row shows the `GitChangeKind` + `Path`) on top/left and the hosted `<views:DiffView DataContext="{Binding Diff}" />` filling the rest. Mirror `IssuesView` styling.

- [ ] **Step 2: Wire `Changes` on the VM** — add `[ObservableProperty] private ChangesViewModel? _changes;`; construct it when the git services are available (`_git` as `IGitService` from Plan 1 + the `IGitDiff` — the shared `GitService` implements both). In `App.axaml.cs`, pass the shared `GitService` as the `IGitDiff` too (add an `IGitDiff? gitDiff = null` param to `InitializeAsync` OR reuse the existing `gitService`/`gitLog` instance — since `GitService` implements all three interfaces, cast the stored `_git` to `IGitDiff` if non-null rather than adding another param; choose the minimal approach after reading how `_git`/`_gitLog` are stored). On `SelectedPane` change to a pane with a `WorktreePath`, fire-and-forget `_ = Changes.LoadAsync(path)` alongside the existing `GitGraph.LoadAsync`.

- [ ] **Step 3: Place changes+diff in the Git tab** — restructure the Git tab content in `MainWindow.axaml` so it shows BOTH the graph (Plan 2a) and the changes/diff. Simplest: a vertical `Grid` with `RowDefinitions="Auto,*"` or a nested `TabControl` ("Graph" | "Changes") inside the Git tab. Prefer a nested `TabControl` (two sub-tabs) to keep each view full-height:
  - sub-tab "History" → `GitGraphView` (DataContext `GitGraph`)
  - sub-tab "Changes" → `ChangesView` (DataContext `Changes`)

- [ ] **Step 4: Run + commit** — `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors; `dotnet test tests/Styloagent.App.Tests/...` → green.

```bash
git add src/Styloagent.App/Views/ChangesView.axaml src/Styloagent.App/Views/ChangesView.axaml.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/App.axaml.cs src/Styloagent.App/Views/MainWindow.axaml
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): Changes sub-tab (files list + diff) in the Git panel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Full-suite green + diff screenshot

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** run Core, App, Git, UITests suites → all pass.
- [ ] **Step 3:** inspect `/tmp/styloagent-diff.png` — red/green line rows render.
- [ ] **Step 4:** commit any incidental fixes with the standard trailer.

---

## Self-Review

**Spec coverage (Plan 2b slice):**
- "See what an agent changed" — changes list + per-file diff → Tasks 4-6. ✓
- Lighter diff control, NO AvaloniaEdit/fork/bump (the user's chosen option) → Tasks 1-2, 5 (plain Avalonia). ✓
- Reuse SourceGit's diff parsing (derived, credited) → Task 2 + THIRD-PARTY.md note. ✓
- Reuse Plan 1's `GitStatus.Changes` for the files list → Task 4 (no re-implementation). ✓
- Diff runner on the git backend → Task 3 (`IGitDiff` on `Styloagent.Git`, Core stays vendored/diff-type-free of the *view*; `DiffModel` is UI-free in Core). ✓
- **Deferred to 2c:** stage/unstage/commit/push/pull/branch/stash write ops + full panel actions; syntax highlighting / side-by-side (the "Full SourceGit diff view" option remains available later).

**Deviation (intentional):** the diff data model (`DiffLine`/`FileDiff`) is authored fresh in `Styloagent.Core.Git` (UI-free), not vendored — the types are trivial and the reusable value (line classification) is in `UnifiedDiffParser`, credited as derived from SourceGit. `IGitDiff` lives in `Styloagent.Git` (like `IGitLog`) so nothing new leaks into Core beyond the UI-free `FileDiff`.

**Placeholder scan:** the parser + model are complete code; the VM/view tasks specify the exact bindings and the awaitable-select approach to keep tests deterministic. The one flagged soft spot (fire-and-forget vs awaitable select) is called out in Task 4 with the resolution (awaitable `SelectFileAsync`).

**Type consistency:** `DiffLine`/`DiffLineKind`/`FileDiff` (Task 1) are used identically in Tasks 2-5; `IGitDiff.GetDiffAsync` (Task 3) is consumed by `ChangesViewModel` (Task 4); `DiffViewModel.File` (Task 4) is bound by `DiffView` (Task 5).
