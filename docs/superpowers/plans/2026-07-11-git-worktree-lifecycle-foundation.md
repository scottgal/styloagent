# Git Worktree Lifecycle + Gated Wrap-up (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each agent an optional isolated git worktree on spawn, and a gated auto-merge "wrap-up" that merges clean/tested work to main and cleans up — or keeps the worktree and files an issue on failure — with no SourceGit dependency yet.

**Architecture:** A UI-free `IGitService` (interface in `Styloagent.Core.Git`, process-backed impl in a new `Styloagent.Git` project) wraps the `git` CLI, mirroring the existing `GitCliReader`. Pure orchestration (`WrapUpService`, `WorktreeNaming`, parsers, policy) lives in `Styloagent.Core` and is fully unit-tested against fakes; the App wires worktree creation into `spawn_agent` and exposes a `wrap_up` MCP tool. This is Plan 1 of 2; Plan 2 vendors SourceGit's visual controls and the git panel on top of `IGitService`.

**Tech Stack:** .NET 10, C#, xUnit, VYaml (YAML config), CommunityToolkit.Mvvm, ModelContextProtocol.AspNetCore. Git CLI ≥ 2.25.1.

## Global Constraints

- Target framework `net10.0`; the new `Styloagent.Git` project is a plain library (NO Avalonia in Plan 1).
- Analyzers run **as errors**. Known gotchas in this repo:
  - MCP tool method names contain underscores → wrap with `#pragma warning disable CA1707` or `[SuppressMessage("Style","CA1707", …)]` (see existing `FleetTools`).
  - Instance members used only for binding that don't touch instance state → `#pragma warning disable CA1822`.
  - No inline array args in asserts where CA1861 fires — hoist to a local or use `Assert.Single`/`Assert.Equal`.
- Git access is process-based and **never throws across the seam**: methods return `GitResult`/`GitResult<T>` with `Ok`/`Error`; a missing git or non-repo path yields a failed result, mirroring `GitCliReader` (which returns empty rather than throwing).
- Resolve the git executable with the existing strategy (Finder-launched apps lack login PATH): probe `/opt/homebrew/bin/git`, `/usr/local/bin/git`, `/usr/bin/git`, else `git`.
- Worktrees live under `<repo>/.worktrees/<slug>`; branches are `agent/<slug>`. Add `.worktrees/` to the repo's `.git/info/exclude` (never edit the user's `.gitignore`).
- Wrap-up is always the gated auto-merge; failure degrades to keep-worktree + file an issue (via existing `Styloagent.Core.Issues.IssueStore.Write`). It never merges dirty, test-failing, or conflicting work.
- Commit with the repo's author/trailer convention:
  `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit …`
  and end each commit message with:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Commit directly to the current branch (no new branches) per project convention.

---

## File Structure

**Create:**
- `src/Styloagent.Core/Git/GitResult.cs` — `GitResult`, `GitResult<T>` result types.
- `src/Styloagent.Core/Git/GitStatus.cs` — `GitStatus`, `GitChange`, `GitChangeKind`.
- `src/Styloagent.Core/Git/IGitService.cs` — the git-operations seam.
- `src/Styloagent.Core/Git/GitStatusParser.cs` — porcelain-v2 → `GitStatus` (pure).
- `src/Styloagent.Core/Git/WorktreeNaming.cs` — `(path, branch)` derivation (pure).
- `src/Styloagent.Core/Git/ITestRunner.cs` — `ITestRunner`, `TestOutcome`.
- `src/Styloagent.Core/Git/WrapUpService.cs` — `WrapUpService`, `WrapUpRequest`, `WrapUpOutcome`, `WrapUpStatus`.
- `src/Styloagent.Core/Projects/GitPolicy.cs` — `GitPolicy` + `GitPolicyReader` (VYaml).
- `src/Styloagent.Git/Styloagent.Git.csproj` — new library project.
- `src/Styloagent.Git/GitService.cs` — process-backed `IGitService`.
- `src/Styloagent.Git/ProcessTestRunner.cs` — process-backed `ITestRunner`.
- `tests/Styloagent.Core.Tests/GitStatusParserTests.cs`
- `tests/Styloagent.Core.Tests/WorktreeNamingTests.cs`
- `tests/Styloagent.Core.Tests/GitPolicyReaderTests.cs`
- `tests/Styloagent.Core.Tests/WrapUpServiceTests.cs`
- `tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj` — opt-in integration tests (real temp repo).
- `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`

**Modify:**
- `src/Styloagent.Core/Mcp/FleetTypes.cs` — add `Worktree` to `SpawnRequest`.
- `src/Styloagent.Core/Mcp/IFleetController.cs` — add `WrapUpAsync(string callerPrefix)`.
- `src/Styloagent.Core/Projects/ProjectConfig.cs` — add `GitPolicyPath`.
- `src/Styloagent.Core/Projects/DefaultTemplates.cs` — document `worktree` param + `wrap_up`.
- `src/Styloagent.App/Mcp/FleetTools.cs` — `spawn_agent` gains `worktree`; add `wrap_up` tool.
- `src/Styloagent.App/Mcp/FleetController.cs` — implement `WrapUpAsync`.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — worktree-on-spawn, `WrapUpAsync`, `IGitService` field.
- `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` — `WorktreePath`, `WorktreeBranch`, `GitBadge`.
- `src/Styloagent.App/App.axaml.cs` — inject `GitService`/`ProcessTestRunner`.
- `src/Styloagent.App/Styloagent.App.csproj` — reference `Styloagent.Git`.
- `tests/Styloagent.App.Tests/FleetToolsTests.cs` + `StyloagentMcpServerTests.cs` — fake controller gains `WrapUpAsync`; new tool tests.
- `styloagent.sln` — add the two new projects.

---

## Task 1: Git result + status types + `IGitService` seam

**Files:**
- Create: `src/Styloagent.Core/Git/GitResult.cs`, `src/Styloagent.Core/Git/GitStatus.cs`, `src/Styloagent.Core/Git/IGitService.cs`
- Test: `tests/Styloagent.Core.Tests/GitStatusParserTests.cs` (placeholder test for the result helpers here; parser added Task 2)

**Interfaces:**
- Produces:
  - `GitResult(bool Ok, string? Error)` with `GitResult.Success()`, `GitResult.Fail(string)`.
  - `GitResult<T>(bool Ok, T? Value, string? Error)` with `.Success(T)`, `.Fail(string)`.
  - `enum GitChangeKind { Added, Modified, Deleted, Renamed, Untracked, Conflicted }`
  - `GitChange(string Path, GitChangeKind Kind)`
  - `GitStatus(bool IsDirty, int Ahead, int Behind, bool HasConflicts, IReadOnlyList<GitChange> Changes)` with `GitStatus.Clean`.
  - `interface IGitService` (methods below).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/GitStatusParserTests.cs`:

```csharp
using Styloagent.Core.Git;
using Xunit;

public class GitStatusParserTests
{
    [Fact]
    public void GitResult_success_and_failure_carry_their_payload()
    {
        var ok = GitResult<int>.Success(5);
        Assert.True(ok.Ok);
        Assert.Equal(5, ok.Value);

        var bad = GitResult.Fail("boom");
        Assert.False(bad.Ok);
        Assert.Equal("boom", bad.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitStatusParserTests"`
Expected: FAIL — `GitResult` / `Styloagent.Core.Git` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/Styloagent.Core/Git/GitResult.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>Result of a git operation that returns no data. Never throws across the seam.</summary>
public sealed record GitResult(bool Ok, string? Error)
{
    public static GitResult Success() => new(true, null);
    public static GitResult Fail(string error) => new(false, error);
}

/// <summary>Result of a git operation that returns a value on success.</summary>
public sealed record GitResult<T>(bool Ok, T? Value, string? Error)
{
    public static GitResult<T> Success(T value) => new(true, value, null);
    public static GitResult<T> Fail(string error) => new(false, default, error);
}
```

Create `src/Styloagent.Core/Git/GitStatus.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>How a single path changed in the working tree.</summary>
public enum GitChangeKind { Added, Modified, Deleted, Renamed, Untracked, Conflicted }

/// <summary>One changed path in a worktree.</summary>
public sealed record GitChange(string Path, GitChangeKind Kind);

/// <summary>A worktree's status: dirtiness, ahead/behind vs upstream, conflicts, and the changes.</summary>
public sealed record GitStatus(
    bool IsDirty, int Ahead, int Behind, bool HasConflicts, IReadOnlyList<GitChange> Changes)
{
    public static GitStatus Clean { get; } = new(false, 0, 0, false, System.Array.Empty<GitChange>());
}
```

Create `src/Styloagent.Core/Git/IGitService.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>
/// The git-operations seam the app/VMs call. Process-backed impl lives in <c>Styloagent.Git</c>;
/// faked in tests. Every method is tolerant — failures surface as a failed <see cref="GitResult"/>,
/// never an exception.
/// </summary>
public interface IGitService
{
    Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default);
    Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default);
    Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default);
    Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default);
    Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitStatusParserTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/GitResult.cs src/Styloagent.Core/Git/GitStatus.cs src/Styloagent.Core/Git/IGitService.cs tests/Styloagent.Core.Tests/GitStatusParserTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): result + status types and IGitService seam

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `GitStatusParser` (porcelain-v2 → `GitStatus`)

**Files:**
- Create: `src/Styloagent.Core/Git/GitStatusParser.cs`
- Test: `tests/Styloagent.Core.Tests/GitStatusParserTests.cs` (extend)

**Interfaces:**
- Consumes: `GitStatus`, `GitChange`, `GitChangeKind` (Task 1).
- Produces: `static GitStatus GitStatusParser.Parse(string porcelainV2Branch)` — parses the output of
  `git status --porcelain=v2 --branch`.

- [ ] **Step 1: Write the failing test** — add to `GitStatusParserTests.cs`:

```csharp
    private const string Sample =
        "# branch.oid abc123\n" +
        "# branch.head agent/foss-\n" +
        "# branch.upstream origin/agent/foss-\n" +
        "# branch.ab +2 -1\n" +
        "1 .M N... 100644 100644 100644 aaa bbb src/Foo.cs\n" +
        "1 A. N... 000000 100644 100644 000 ccc src/Bar.cs\n" +
        "? notes.md\n";

    [Fact]
    public void Parse_reads_ahead_behind_and_changes()
    {
        var s = GitStatusParser.Parse(Sample);
        Assert.True(s.IsDirty);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.False(s.HasConflicts);
        Assert.Equal(3, s.Changes.Count);
        Assert.Contains(s.Changes, c => c.Path == "src/Bar.cs" && c.Kind == GitChangeKind.Added);
        Assert.Contains(s.Changes, c => c.Path == "notes.md" && c.Kind == GitChangeKind.Untracked);
    }

    [Fact]
    public void Parse_flags_conflicts_and_clean()
    {
        var conflict = GitStatusParser.Parse("# branch.ab +0 -0\nu UU N... 1 2 3 4 h1 h2 h3 src/Clash.cs\n");
        Assert.True(conflict.HasConflicts);
        Assert.Contains(conflict.Changes, c => c.Kind == GitChangeKind.Conflicted);

        var clean = GitStatusParser.Parse("# branch.oid abc\n# branch.head main\n# branch.ab +0 -0\n");
        Assert.False(clean.IsDirty);
        Assert.Empty(clean.Changes);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitStatusParserTests"`
Expected: FAIL — `GitStatusParser` does not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Git/GitStatusParser.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>Parses <c>git status --porcelain=v2 --branch</c> output into a <see cref="GitStatus"/>.</summary>
public static class GitStatusParser
{
    public static GitStatus Parse(string porcelainV2Branch)
    {
        int ahead = 0, behind = 0;
        bool hasConflicts = false;
        var changes = new List<GitChange>();

        foreach (var raw in porcelainV2Branch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("# branch.ab ", System.StringComparison.Ordinal))
            {
                foreach (var tok in line["# branch.ab ".Length..].Split(' '))
                {
                    if (tok.StartsWith('+') && int.TryParse(tok[1..], out var a)) ahead = a;
                    else if (tok.StartsWith('-') && int.TryParse(tok[1..], out var b)) behind = b;
                }
                continue;
            }
            if (line[0] == '#') continue;
            if (line[0] == '!') continue;                       // ignored
            if (line[0] == '?') { changes.Add(new GitChange(line[2..].Trim(), GitChangeKind.Untracked)); continue; }
            if (line[0] == 'u') { hasConflicts = true; changes.Add(new GitChange(PathOf(line), GitChangeKind.Conflicted)); continue; }
            if (line[0] == '1' || line[0] == '2')
            {
                var xy = line.Length >= 4 ? line.Substring(2, 2) : "..";
                changes.Add(new GitChange(PathOf(line), KindFromXy(xy, renamed: line[0] == '2')));
            }
        }

        return new GitStatus(changes.Count > 0, ahead, behind, hasConflicts, changes);
    }

    // porcelain v2: the path is the final whitespace-delimited field; renames put "new\told" with a TAB.
    private static string PathOf(string line)
    {
        int tab = line.IndexOf('\t');
        string head = tab >= 0 ? line[..tab] : line;
        int lastSpace = head.LastIndexOf(' ');
        return lastSpace >= 0 ? head[(lastSpace + 1)..] : head;
    }

    private static GitChangeKind KindFromXy(string xy, bool renamed)
    {
        if (renamed) return GitChangeKind.Renamed;
        if (xy.Contains('A')) return GitChangeKind.Added;
        if (xy.Contains('D')) return GitChangeKind.Deleted;
        return GitChangeKind.Modified;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitStatusParserTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/GitStatusParser.cs tests/Styloagent.Core.Tests/GitStatusParserTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): porcelain-v2 status parser

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `Styloagent.Git` project + `GitService` (process-backed)

**Files:**
- Create: `src/Styloagent.Git/Styloagent.Git.csproj`, `src/Styloagent.Git/GitService.cs`
- Create: `tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj`, `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`
- Modify: `styloagent.sln`

**Interfaces:**
- Consumes: `IGitService`, `GitResult`, `GitStatus`, `GitStatusParser` (Tasks 1-2).
- Produces: `public sealed class GitService : IGitService`.

- [ ] **Step 1: Create the project + solution wiring**

Create `src/Styloagent.Git/Styloagent.Git.csproj` (copy `TargetFramework`/analyzer settings from `src/Styloagent.Core/Styloagent.Core.csproj`; NO Avalonia):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Styloagent.Core/Styloagent.Core.csproj" />
  </ItemGroup>
</Project>
```

Add both projects to the solution:

```bash
dotnet sln styloagent.sln add src/Styloagent.Git/Styloagent.Git.csproj
dotnet sln styloagent.sln add tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj
```

- [ ] **Step 2: Write the failing integration test**

Create `tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj` (mirror `tests/Styloagent.Core.Tests/*.csproj`, add a ProjectReference to `Styloagent.Git`). Then `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs`:

```csharp
using System.Diagnostics;
using Styloagent.Core.Git;
using Xunit;

public class GitServiceIntegrationTests
{
    // Skip cleanly when git is unavailable so CI without git stays green.
    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void Run(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task AddWorktree_then_status_then_merge_and_remove()
    {
        if (!GitAvailable()) return;

        var repo = Path.Combine(Path.GetTempPath(), "gitsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main");
            Run(repo, "config user.email t@t.t");
            Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
            Run(repo, "add -A");
            Run(repo, "commit -m init");

            var git = new Styloagent.Git.GitService();
            var wt = Path.Combine(repo, ".worktrees", "foss");

            var add = await git.AddWorktreeAsync(repo, wt, "agent/foss");
            Assert.True(add.Ok, add.Error);
            Assert.True(Directory.Exists(wt));

            File.WriteAllText(Path.Combine(wt, "b.txt"), "two");
            Run(wt, "add -A");
            Run(wt, "commit -m work");

            var status = await git.GetStatusAsync(wt);
            Assert.True(status.Ok);
            Assert.False(status.Value!.IsDirty);

            var merge = await git.MergeNoFfAsync(repo, "agent/foss", "main");
            Assert.True(merge.Ok, merge.Error);
            Assert.True(File.Exists(Path.Combine(repo, "b.txt")));

            var remove = await git.RemoveWorktreeAsync(repo, wt);
            Assert.True(remove.Ok, remove.Error);
            Assert.False(Directory.Exists(wt));
        }
        finally { TryDeleteRepo(repo); }
    }

    private static void TryDeleteRepo(string repo)
    {
        try { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); } catch { }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj`
Expected: FAIL — `Styloagent.Git.GitService` does not exist.

- [ ] **Step 4: Write minimal implementation** — create `src/Styloagent.Git/GitService.cs`:

```csharp
using System.Diagnostics;
using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>
/// <see cref="IGitService"/> backed by the <c>git</c> CLI. Never throws: failures surface as a
/// failed <see cref="GitResult"/> carrying git's stderr. Mirrors GitCliReader's process pattern.
/// </summary>
public sealed class GitService : IGitService
{
    public async Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ct, "status", "--porcelain=v2", "--branch");
        return r.Ok ? GitResult<GitStatus>.Success(GitStatusParser.Parse(r.Stdout)) : GitResult<GitStatus>.Fail(r.Stderr);
    }

    public async Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "worktree", "add", worktreePath, "-b", newBranch));

    public async Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "worktree", "remove", "--force", worktreePath));

    public async Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default)
    {
        var checkout = await RunAsync(repoRoot, ct, "checkout", intoBranch);
        if (!checkout.Ok) return GitResult.Fail(checkout.Stderr);
        return ToResult(await RunAsync(repoRoot, ct, "merge", "--no-ff", "--no-edit", sourceBranch));
    }

    public async Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "merge", "--abort"));

    public async Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "branch", force ? "-D" : "-d", branch));

    private static GitResult ToResult(ProcOutcome p) => p.Ok ? GitResult.Success() : GitResult.Fail(p.Stderr);

    private readonly record struct ProcOutcome(bool Ok, string Stdout, string Stderr);

    private static async Task<ProcOutcome> RunAsync(string workingDir, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveGit())
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return new ProcOutcome(false, "", "failed to start git");
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return new ProcOutcome(proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcOutcome(false, "", ex.Message);
        }
    }

    // Finder-launched .apps don't inherit the login PATH; resolve git explicitly (matches GitCliReader).
    private static string ResolveGit()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;
        return "git";
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj`
Expected: PASS (1 test; skips silently if git is unavailable).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Git/ tests/Styloagent.Git.Tests/ styloagent.sln
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): Styloagent.Git project + process-backed GitService

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `GitPolicy` + reader + `ProjectConfig.GitPolicyPath`

**Files:**
- Create: `src/Styloagent.Core/Projects/GitPolicy.cs`
- Modify: `src/Styloagent.Core/Projects/ProjectConfig.cs`
- Test: `tests/Styloagent.Core.Tests/GitPolicyReaderTests.cs`

**Interfaces:**
- Produces:
  - `GitPolicy(string? TestCommand, bool RemoveWorktreeOnMerge, string MainBranch)` with `GitPolicy.Default`
    (`TestCommand: null, RemoveWorktreeOnMerge: true, MainBranch: "main"`).
  - `static GitPolicy GitPolicyReader.Read(string path)` — tolerant, defaults on missing/invalid.
  - `ProjectConfig.GitPolicyPath` = `<cfg>/git-policy.yaml`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/GitPolicyReaderTests.cs`:

```csharp
using Styloagent.Core.Projects;
using Xunit;

public class GitPolicyReaderTests
{
    [Fact]
    public void Missing_file_returns_defaults()
    {
        var p = GitPolicyReader.Read(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Assert.Null(p.TestCommand);
        Assert.True(p.RemoveWorktreeOnMerge);
        Assert.Equal("main", p.MainBranch);
    }

    [Fact]
    public void Reads_values_from_yaml()
    {
        var file = Path.Combine(Path.GetTempPath(), "gitpol-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(file, "testCommand: dotnet test\nremoveWorktreeOnMerge: false\nmainBranch: trunk\n");
        try
        {
            var p = GitPolicyReader.Read(file);
            Assert.Equal("dotnet test", p.TestCommand);
            Assert.False(p.RemoveWorktreeOnMerge);
            Assert.Equal("trunk", p.MainBranch);
        }
        finally { File.Delete(file); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitPolicyReaderTests"`
Expected: FAIL — `GitPolicy`/`GitPolicyReader` do not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Projects/GitPolicy.cs` (mirrors `PriorityPolicy.cs`):

```csharp
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>
/// Per-project git behaviour, read from <c>.styloagent/git-policy.yaml</c>. Governs the gated
/// wrap-up: which tests to run before merge, whether to remove the worktree after a clean merge,
/// and the merge target branch. All optional.
/// </summary>
public sealed record GitPolicy(string? TestCommand, bool RemoveWorktreeOnMerge, string MainBranch)
{
    public static GitPolicy Default { get; } = new(TestCommand: null, RemoveWorktreeOnMerge: true, MainBranch: "main");
}

/// <summary>YAML surface for <see cref="GitPolicy"/>.</summary>
[YamlObject]
internal partial class GitPolicyFile
{
    public string? TestCommand { get; set; }
    public bool? RemoveWorktreeOnMerge { get; set; }
    public string? MainBranch { get; set; }
}

/// <summary>Tolerant reader: defaults on missing/invalid, never throws.</summary>
public static class GitPolicyReader
{
    public static GitPolicy Read(string path)
    {
        var d = GitPolicy.Default;
        try
        {
            if (!File.Exists(path)) return d;
            var file = YamlSerializer.Deserialize<GitPolicyFile>(File.ReadAllBytes(path));
            return new GitPolicy(
                TestCommand: string.IsNullOrWhiteSpace(file.TestCommand) ? d.TestCommand : file.TestCommand!.Trim(),
                RemoveWorktreeOnMerge: file.RemoveWorktreeOnMerge ?? d.RemoveWorktreeOnMerge,
                MainBranch: string.IsNullOrWhiteSpace(file.MainBranch) ? d.MainBranch : file.MainBranch!.Trim());
        }
        catch { return d; }
    }
}
```

Modify `src/Styloagent.Core/Projects/ProjectConfig.cs` — add the parameter and the `For` mapping (append after `IssuesDir`):

```csharp
    string IssuesDir,
    string GitPolicyPath)
```

and in `For`:

```csharp
            IssuesDir: Path.Combine(cfg, "issues"),
            GitPolicyPath: Path.Combine(cfg, "git-policy.yaml"));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitPolicyReaderTests"`
Expected: PASS (2 tests). (If `ProjectConfig.For` is called with positional args anywhere, the compiler will flag it — it is only called in `ProjectConfig.For` itself, which we updated.)

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Projects/GitPolicy.cs src/Styloagent.Core/Projects/ProjectConfig.cs tests/Styloagent.Core.Tests/GitPolicyReaderTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): git-policy.yaml (test command, merge target, worktree cleanup)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `WorktreeNaming` (path + branch derivation)

**Files:**
- Create: `src/Styloagent.Core/Git/WorktreeNaming.cs`
- Test: `tests/Styloagent.Core.Tests/WorktreeNamingTests.cs`

**Interfaces:**
- Produces: `static (string Path, string Branch) WorktreeNaming.For(string repoRoot, string prefix, IEnumerable<string> existingPaths)`.
  Path = `<repoRoot>/.worktrees/<slug>`, Branch = `agent/<slug>`, de-duplicated with a `-N` suffix when the path is taken.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/WorktreeNamingTests.cs`:

```csharp
using Styloagent.Core.Git;
using Xunit;

public class WorktreeNamingTests
{
    [Fact]
    public void Derives_path_and_branch_from_prefix()
    {
        var (path, branch) = WorktreeNaming.For("/repo", "foss-", System.Array.Empty<string>());
        Assert.Equal(Path.Combine("/repo", ".worktrees", "foss"), path);
        Assert.Equal("agent/foss", branch);
    }

    [Fact]
    public void Deduplicates_when_path_exists()
    {
        var existing = new[] { Path.Combine("/repo", ".worktrees", "foss") };
        var (path, branch) = WorktreeNaming.For("/repo", "foss-", existing);
        Assert.Equal(Path.Combine("/repo", ".worktrees", "foss-2"), path);
        Assert.Equal("agent/foss-2", branch);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~WorktreeNamingTests"`
Expected: FAIL — `WorktreeNaming` does not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Git/WorktreeNaming.cs`:

```csharp
using System.Text;

namespace Styloagent.Core.Git;

/// <summary>Derives a worktree checkout path and branch name for an agent prefix (pure).</summary>
public static class WorktreeNaming
{
    public static (string Path, string Branch) For(string repoRoot, string prefix, IEnumerable<string> existingPaths)
    {
        var slug = Slug(prefix);
        var baseDir = Path.Combine(repoRoot, ".worktrees", slug);
        var taken = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);

        var path = baseDir;
        var branch = $"agent/{slug}";
        int n = 1;
        while (taken.Contains(path))
        {
            n++;
            path = $"{baseDir}-{n}";
            branch = $"agent/{slug}-{n}";
        }
        return (path, branch);
    }

    private static string Slug(string prefix)
    {
        var sb = new StringBuilder();
        foreach (var c in prefix.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "agent" : s;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~WorktreeNamingTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/WorktreeNaming.cs tests/Styloagent.Core.Tests/WorktreeNamingTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): worktree path/branch naming with de-duplication

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Thread `worktree` through `spawn_agent`

**Files:**
- Modify: `src/Styloagent.Core/Mcp/FleetTypes.cs`, `src/Styloagent.App/Mcp/FleetTools.cs`,
  `src/Styloagent.Core/Projects/DefaultTemplates.cs`
- Modify (call sites): `tests/Styloagent.App.Tests/FleetHudUpdateTests.cs`, `tests/Styloagent.App.Tests/FleetSpawnTests.cs`
- Test: `tests/Styloagent.App.Tests/FleetToolsTests.cs`

**Interfaces:**
- Produces: `SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree)`;
  `FleetTools.spawn_agent(string prefix, string responsibility, string dir, string launchPrompt, bool worktree)`.

- [ ] **Step 1: Write the failing test** — add to `tests/Styloagent.App.Tests/FleetToolsTests.cs`:

```csharp
    [Fact]
    public async Task spawn_agent_passes_the_worktree_flag_through()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.spawn_agent("foss-", "owns FOSS", ".", "You are foss-.", worktree: true);

        Assert.True(ctrl.LastReq!.Worktree);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~FleetToolsTests"`
Expected: FAIL — `spawn_agent` has no `worktree` parameter / `SpawnRequest` has no `Worktree`.

- [ ] **Step 3: Write minimal implementation**

`src/Styloagent.Core/Mcp/FleetTypes.cs` — extend the record:

```csharp
public sealed record SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree);
```

`src/Styloagent.App/Mcp/FleetTools.cs` — update the tool signature, description, and `SpawnRequest` construction:

```csharp
    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'. Set worktree=true when this agent's work overlaps files another agent owns, so it runs isolated on its own git worktree/branch; otherwise false to share the repo.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt, bool worktree)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = McpAuth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt, worktree));

        return outcome.Spawned ? outcome.Message : $"rejected: {outcome.Message}";
    }
```

Update the two test call sites to pass the new positional arg:
- `tests/Styloagent.App.Tests/FleetHudUpdateTests.cs:31` →
  `new SpawnRequest(parentPrefix, "hud-", "r", ".", "p", false)`
- `tests/Styloagent.App.Tests/FleetSpawnTests.cs:25` →
  `new SpawnRequest(overviewPrefix, "newsub-", "owns X", ".", "You are newsub-.", false)`
- `tests/Styloagent.App.Tests/FleetSpawnTests.cs:46` →
  `new SpawnRequest(vm.Panes[0].Prefix, "x-", "r", ".", "p", false)`

Update `src/Styloagent.Core/Projects/DefaultTemplates.cs` — in the `spawn_agent` bullet of `SystemPrompt`, append the worktree rule:

```
- `spawn_agent(prefix, responsibility, dir, launchPrompt, worktree)` — launches a child agent under
  you. Set `worktree: true` **only** when the new agent's responsibility overlaps files an existing
  agent owns (so it works isolated on its own `agent/<prefix>` worktree); otherwise `false` to share
  the repo. You decide this from the fleet + architecture.
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~FleetToolsTests"`
Expected: PASS. Also build the App test project so the updated call sites compile:
`dotnet build tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Mcp/FleetTypes.cs src/Styloagent.App/Mcp/FleetTools.cs src/Styloagent.Core/Projects/DefaultTemplates.cs tests/Styloagent.App.Tests/FleetToolsTests.cs tests/Styloagent.App.Tests/FleetHudUpdateTests.cs tests/Styloagent.App.Tests/FleetSpawnTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): spawn_agent gains worktree flag (overview decides on overlap)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Create the worktree on spawn (App wiring)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`,
  `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs`,
  `src/Styloagent.App/App.axaml.cs`, `src/Styloagent.App/Styloagent.App.csproj`
- Test: `tests/Styloagent.Core.Tests/WorktreeNamingTests.cs` already covers the pure derivation; add an App-level guard test in `tests/Styloagent.App.Tests/FleetSpawnTests.cs`.

**Interfaces:**
- Consumes: `IGitService` (Task 1), `WorktreeNaming.For` (Task 5), `GitService` (Task 3), `SpawnRequest.Worktree` (Task 6).
- Produces: `AgentPaneViewModel.WorktreePath` (string?), `AgentPaneViewModel.WorktreeBranch` (string?);
  `MainWindowViewModel` now holds an `IGitService? _git` set via `InitializeAsync(..., IGitService? gitService = null, ...)`.

- [ ] **Step 1: Write the failing test** — add to `tests/Styloagent.App.Tests/FleetSpawnTests.cs` (a fake `IGitService` records the add and the pane carries the branch). Add this fake near the top of the test class:

```csharp
    private sealed class RecordingGitService : Styloagent.Core.Git.IGitService
    {
        public string? AddedBranch;
        public Task<Styloagent.Core.Git.GitResult<Styloagent.Core.Git.GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
            => Task.FromResult(Styloagent.Core.Git.GitResult<Styloagent.Core.Git.GitStatus>.Success(Styloagent.Core.Git.GitStatus.Clean));
        public Task<Styloagent.Core.Git.GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        { AddedBranch = newBranch; Directory.CreateDirectory(worktreePath); return Task.FromResult(Styloagent.Core.Git.GitResult.Success()); }
        public Task<Styloagent.Core.Git.GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default) => Task.FromResult(Styloagent.Core.Git.GitResult.Success());
        public Task<Styloagent.Core.Git.GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default) => Task.FromResult(Styloagent.Core.Git.GitResult.Success());
        public Task<Styloagent.Core.Git.GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default) => Task.FromResult(Styloagent.Core.Git.GitResult.Success());
        public Task<Styloagent.Core.Git.GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default) => Task.FromResult(Styloagent.Core.Git.GitResult.Success());
    }
```

Then a test that spawns with `worktree: true` and asserts the pane got an `agent/…` branch. Model it on the existing `FleetSpawnTests` setup (which already builds a VM via `InitializeAsync` and calls `SpawnChild`); pass the `RecordingGitService` into `InitializeAsync` and a real `repoRoot`:

```csharp
    [Fact]
    public async Task Spawn_with_worktree_creates_an_agent_branch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "spawnwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new RecordingGitService();
        try
        {
            // Build the VM exactly as the other FleetSpawnTests do, but pass gitService: git and repoRoot: repo,
            // then attach a project rooted at repo so _project.Root == repo.
            var vm = await BuildOverviewVmAsync(repoRoot: repo, gitService: git);   // helper mirrors existing setup
            vm.AttachProject(Styloagent.Core.Projects.ProjectConfig.For(repo));

            var outcome = vm.SpawnChild(new SpawnRequest(vm.Panes[0].Prefix, "iso-", "overlaps foss", ".", "p", worktree: true));

            Assert.True(outcome.Spawned);
            Assert.Equal("agent/iso", git.AddedBranch);
            var pane = vm.Panes.First(p => p.Prefix == "iso-");
            Assert.Equal("agent/iso", pane.WorktreeBranch);
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
```

> Note: if `FleetSpawnTests` builds the VM inline rather than via a helper, extract a small `BuildOverviewVmAsync(repoRoot, gitService)` local that reproduces the existing setup and pass the two new args. Keep the existing tests working by defaulting `gitService: null`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~Spawn_with_worktree"`
Expected: FAIL — `InitializeAsync` has no `gitService` param; `AgentPaneViewModel` has no `WorktreeBranch`; `SpawnChild` ignores `Worktree`.

- [ ] **Step 3: Write minimal implementation**

`src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` — add two nullable properties (plain auto-properties; if analyzers require, mark with `#pragma warning disable CA1822` is not needed for properties):

```csharp
    /// <summary>The agent's dedicated git worktree checkout path, or null if it shares the repo.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>The agent's dedicated branch (agent/&lt;slug&gt;), or null if it shares the repo.</summary>
    public string? WorktreeBranch { get; set; }
```

`src/Styloagent.App/ViewModels/MainWindowViewModel.cs`:

1. Add a field near the other injected services (e.g. by `_launcher`): `private IGitService? _git;`
   and add `using Styloagent.Core.Git;` if not present.
2. Add a parameter to `InitializeAsync` (default null so existing callers/tests compile) and assign it:

```csharp
    public static async Task<MainWindowViewModel> InitializeAsync(
        string channelRoot,
        IPtyLauncher launcher,
        IFileWatcher watcher,
        IGitReader? gitReader = null,
        string? repoRoot = null,
        string? presentationPath = null,
        string? overviewSystemPromptPath = null,
        IGitService? gitService = null,
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
        vm._launcher = launcher;
        vm._watcher = watcher;
        vm._git = gitService;
        // …existing body…
```

3. In `SpawnChild`, before creating the pane, create the worktree when requested and pass the resolved dir/branch into `CreatePaneForProposed`. Replace the existing pane-creation call:

```csharp
    public SpawnOutcome SpawnChild(SpawnRequest req)
    {
        var state = new FleetState(BuildFleetSnapshot().Members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
        var decision = FleetGovernor.Check(state, req.ParentPrefix, req.Prefix);
        if (!decision.Allowed) return SpawnOutcome.Reject(decision.Reason!.Value, decision.Message);

        int parentDepth = Panes.First(p => p.Prefix == req.ParentPrefix).Depth;

        string? worktreePath = null, worktreeBranch = null;
        if (req.Worktree && _git is not null && _project is not null)
        {
            var existing = Panes.Where(p => p.WorktreePath is not null).Select(p => p.WorktreePath!);
            var (path, branch) = WorktreeNaming.For(_project.Root, req.Prefix, existing);
            var add = _git.AddWorktreeAsync(_project.Root, path, branch).GetAwaiter().GetResult();
            if (!add.Ok)
                return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {add.Error}");
            EnsureWorktreesIgnored(_project.Root);
            worktreePath = path;
            worktreeBranch = branch;
        }

        var proposed = new ProposedAgent(req.Prefix, req.Responsibility, req.Dir, req.LaunchPrompt);
        var paneVm = CreatePaneForProposed(proposed, parentPrefix: req.ParentPrefix, depth: parentDepth + 1,
            worktreeOverride: worktreePath, worktreeBranch: worktreeBranch);
        return paneVm is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(req.Prefix);
    }
```

4. Extend `CreatePaneForProposed` signature and use the override for the working dir + stamp the branch. Change its signature and the `entry`/pane construction:

```csharp
    private AgentPaneViewModel? CreatePaneForProposed(
        ProposedAgent p,
        string? parentPrefix = null,
        int depth = 0,
        string? worktreeOverride = null,
        string? worktreeBranch = null)
    {
        // …unchanged guards + launch-prompt persistence…

        string resolvedWorktree = worktreeOverride ?? WorkingDirectoryResolver.Resolve(
            string.IsNullOrWhiteSpace(p.Dir) ? null : Path.Combine(root, p.Dir),
            DefaultWorkingDirectory());

        var entry = new AgentManifestEntry(
            Prefix: p.Prefix,
            Repo: root,
            Worktree: resolvedWorktree,
            LaunchPromptPath: launchPromptPath,
            RestartPromptPath: string.Empty,
            SavedContextPath: string.Empty,
            Transport: AgentTransport.Local);

        // …unchanged session + paneVm construction…
        // after paneVm is constructed, before Panes.Add(paneVm):
        paneVm.WorktreePath = worktreeOverride;
        paneVm.WorktreeBranch = worktreeBranch;
        // …rest unchanged…
    }
```

5. Add the `.git/info/exclude` helper near the bottom of the class:

```csharp
    /// <summary>Ensures .worktrees/ is git-ignored via .git/info/exclude (never touches the user's .gitignore).</summary>
    private static void EnsureWorktreesIgnored(string repoRoot)
    {
        try
        {
            var exclude = Path.Combine(repoRoot, ".git", "info", "exclude");
            if (!File.Exists(exclude)) return;
            var lines = File.ReadAllLines(exclude);
            if (lines.Any(l => l.Trim() == ".worktrees/")) return;
            File.AppendAllText(exclude, Environment.NewLine + ".worktrees/" + Environment.NewLine);
        }
        catch { /* ignoring is best-effort */ }
    }
```

`src/Styloagent.App/App.axaml.cs` — pass a real `GitService` into `InitializeAsync` (add `gitService: new Styloagent.Git.GitService()` to the call around line 37-43):

```csharp
                    var vm = await MainWindowViewModel.InitializeAsync(
                        cfg.ChannelRoot,
                        new PortaPtyLauncher(),
                        new FileSystemFileWatcher(),
                        gitReader: null,
                        repoRoot: cfg.Root,
                        overviewSystemPromptPath: cfg.SystemPromptPath,
                        gitService: new Styloagent.Git.GitService());
```

`src/Styloagent.App/Styloagent.App.csproj` — add the project reference (next to the other `ProjectReference`s):

```xml
    <ProjectReference Include="../Styloagent.Git/Styloagent.Git.csproj" />
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~Spawn_with_worktree"`
Expected: PASS. Also run the full App suite to confirm no regression:
`dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj` → all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ tests/Styloagent.App.Tests/FleetSpawnTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): create agent/<prefix> worktree on spawn when overlap flagged

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: `ITestRunner` + `ProcessTestRunner`

**Files:**
- Create: `src/Styloagent.Core/Git/ITestRunner.cs`, `src/Styloagent.Git/ProcessTestRunner.cs`
- Test: `tests/Styloagent.Git.Tests/GitServiceIntegrationTests.cs` (add a runner test) or a new file `tests/Styloagent.Git.Tests/ProcessTestRunnerTests.cs`

**Interfaces:**
- Produces:
  - `TestOutcome(bool Passed, string Output)`.
  - `interface ITestRunner { Task<TestOutcome> RunAsync(string workingDir, string command, CancellationToken ct = default); }`
  - `ProcessTestRunner : ITestRunner` — runs `command` via the shell in `workingDir`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Git.Tests/ProcessTestRunnerTests.cs`:

```csharp
using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

public class ProcessTestRunnerTests
{
    [Fact]
    public async Task Passing_command_reports_passed()
    {
        var r = new ProcessTestRunner();
        var outcome = await r.RunAsync(Path.GetTempPath(), "exit 0");
        Assert.True(outcome.Passed);
    }

    [Fact]
    public async Task Failing_command_reports_not_passed_with_output()
    {
        var r = new ProcessTestRunner();
        var outcome = await r.RunAsync(Path.GetTempPath(), "echo nope 1>&2; exit 1");
        Assert.False(outcome.Passed);
        Assert.Contains("nope", outcome.Output);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~ProcessTestRunnerTests"`
Expected: FAIL — `ITestRunner`/`ProcessTestRunner` do not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Git/ITestRunner.cs`:

```csharp
namespace Styloagent.Core.Git;

/// <summary>Result of running a project's test command before wrap-up.</summary>
public sealed record TestOutcome(bool Passed, string Output);

/// <summary>Runs a project's configured test command in a worktree. Faked in tests.</summary>
public interface ITestRunner
{
    Task<TestOutcome> RunAsync(string workingDir, string command, CancellationToken ct = default);
}
```

Create `src/Styloagent.Git/ProcessTestRunner.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Styloagent.Core.Git;

namespace Styloagent.Git;

/// <summary>Runs the test command through the platform shell, capturing combined output.</summary>
public sealed class ProcessTestRunner : ITestRunner
{
    public async Task<TestOutcome> RunAsync(string workingDir, string command, CancellationToken ct = default)
    {
        try
        {
            var (shell, flag) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("cmd.exe", "/c")
                : ("/bin/sh", "-c");

            var psi = new ProcessStartInfo(shell)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc is null) return new TestOutcome(false, "failed to start test process");

            var sb = new StringBuilder();
            sb.Append(await proc.StandardOutput.ReadToEndAsync(ct));
            sb.Append(await proc.StandardError.ReadToEndAsync(ct));
            await proc.WaitForExitAsync(ct);
            return new TestOutcome(proc.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return new TestOutcome(false, ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --filter "FullyQualifiedName~ProcessTestRunnerTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/ITestRunner.cs src/Styloagent.Git/ProcessTestRunner.cs tests/Styloagent.Git.Tests/ProcessTestRunnerTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): ITestRunner + shell-backed ProcessTestRunner

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: `WrapUpService` (gated auto-merge state machine)

**Files:**
- Create: `src/Styloagent.Core/Git/WrapUpService.cs`
- Test: `tests/Styloagent.Core.Tests/WrapUpServiceTests.cs`

**Interfaces:**
- Consumes: `IGitService`, `ITestRunner`, `GitPolicy` (Task 4), `Styloagent.Core.Issues.IssueStore` (existing).
- Produces:
  - `WrapUpRequest(string Prefix, string RepoRoot, string WorktreePath, string Branch)`.
  - `enum WrapUpStatus { Merged, KeptUncommitted, KeptTestsFailed, KeptConflict }`.
  - `WrapUpOutcome(WrapUpStatus Status, string Message, string? IssueId)` with `bool Merged => Status == WrapUpStatus.Merged`.
  - `WrapUpService(IGitService git, ITestRunner tests)` with
    `Task<WrapUpOutcome> WrapUpAsync(WrapUpRequest req, GitPolicy policy, string issuesDir, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/WrapUpServiceTests.cs`:

```csharp
using Styloagent.Core.Git;
using Styloagent.Core.Issues;
using Styloagent.Core.Projects;
using Xunit;

public class WrapUpServiceTests
{
    private sealed class FakeGit : IGitService
    {
        public GitStatus Status = GitStatus.Clean;
        public bool MergeOk = true;
        public bool Removed, BranchDeleted, MergeAborted;

        public Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(Status));
        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default) { Removed = true; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default)
            => Task.FromResult(MergeOk ? GitResult.Success() : GitResult.Fail("CONFLICT (content): a.txt"));
        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default) { MergeAborted = true; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default) { BranchDeleted = true; return Task.FromResult(GitResult.Success()); }
    }

    private sealed class FakeTests : ITestRunner
    {
        public bool Pass = true;
        public Task<TestOutcome> RunAsync(string dir, string cmd, CancellationToken ct = default)
            => Task.FromResult(new TestOutcome(Pass, Pass ? "ok" : "FAILED: 1 test"));
    }

    private static (WrapUpRequest req, string issues) Fixture()
        => (new WrapUpRequest("foss-", "/repo", "/repo/.worktrees/foss", "agent/foss"),
            Path.Combine(Path.GetTempPath(), "wrapup-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Clean_and_green_merges_and_cleans_up()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit(); var tests = new FakeTests();
        var svc = new WrapUpService(git, tests);
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy("dotnet test", true, "main"), issues);
            Assert.Equal(WrapUpStatus.Merged, outcome.Status);
            Assert.True(git.Removed);
            Assert.True(git.BranchDeleted);
            Assert.Null(outcome.IssueId);
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Dirty_worktree_is_kept_and_not_merged()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit { Status = new GitStatus(true, 0, 0, false, System.Array.Empty<GitChange>()) };
        var svc = new WrapUpService(git, new FakeTests());
        var outcome = await svc.WrapUpAsync(req, GitPolicy.Default, issues);
        Assert.Equal(WrapUpStatus.KeptUncommitted, outcome.Status);
        Assert.False(git.Removed);
    }

    [Fact]
    public async Task Failing_tests_keep_worktree_and_file_an_issue()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit();
        var svc = new WrapUpService(git, new FakeTests { Pass = false });
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy("dotnet test", true, "main"), issues);
            Assert.Equal(WrapUpStatus.KeptTestsFailed, outcome.Status);
            Assert.False(git.Removed);
            Assert.NotNull(outcome.IssueId);
            Assert.Single(IssueStore.Read(issues));
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Merge_conflict_aborts_keeps_worktree_and_files_an_issue()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit { MergeOk = false };
        var svc = new WrapUpService(git, new FakeTests());
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy(null, true, "main"), issues);
            Assert.Equal(WrapUpStatus.KeptConflict, outcome.Status);
            Assert.True(git.MergeAborted);
            Assert.False(git.Removed);
            Assert.Single(IssueStore.Read(issues));
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~WrapUpServiceTests"`
Expected: FAIL — `WrapUpService` and its types do not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Git/WrapUpService.cs`:

```csharp
using Styloagent.Core.Issues;
using Styloagent.Core.Projects;

namespace Styloagent.Core.Git;

/// <summary>What to wrap up: the agent, its repo, its worktree, and its branch.</summary>
public sealed record WrapUpRequest(string Prefix, string RepoRoot, string WorktreePath, string Branch);

/// <summary>How a wrap-up ended.</summary>
public enum WrapUpStatus { Merged, KeptUncommitted, KeptTestsFailed, KeptConflict }

/// <summary>Outcome of a wrap-up; on a kept failure carries the filed issue id.</summary>
public sealed record WrapUpOutcome(WrapUpStatus Status, string Message, string? IssueId)
{
    public bool Merged => Status == WrapUpStatus.Merged;
}

/// <summary>
/// Gated auto-merge: guard clean → run tests → merge → clean up. Any failure keeps the worktree and
/// (for test/conflict failures) files a high-severity issue. Never merges dirty/failing/conflicting work.
/// </summary>
public sealed class WrapUpService
{
    private readonly IGitService _git;
    private readonly ITestRunner _tests;

    public WrapUpService(IGitService git, ITestRunner tests) => (_git, _tests) = (git, tests);

    public async Task<WrapUpOutcome> WrapUpAsync(WrapUpRequest req, GitPolicy policy, string issuesDir, CancellationToken ct = default)
    {
        // 1. Guard clean.
        var status = await _git.GetStatusAsync(req.WorktreePath, ct);
        if (status.Ok && status.Value!.IsDirty)
            return new WrapUpOutcome(WrapUpStatus.KeptUncommitted,
                $"{req.Prefix} has uncommitted changes — commit or discard before wrap-up.", null);

        // 2. Run tests (if configured).
        if (!string.IsNullOrWhiteSpace(policy.TestCommand))
        {
            var test = await _tests.RunAsync(req.WorktreePath, policy.TestCommand!, ct);
            if (!test.Passed)
            {
                var id = FileIssue(issuesDir, req, $"wrap-up blocked: tests failed on {req.Branch}", test.Output);
                return new WrapUpOutcome(WrapUpStatus.KeptTestsFailed,
                    $"tests failed for {req.Prefix}; worktree kept, issue {id} filed.", id);
            }
        }

        // 3. Merge.
        var merge = await _git.MergeNoFfAsync(req.RepoRoot, req.Branch, policy.MainBranch, ct);
        if (!merge.Ok)
        {
            await _git.AbortMergeAsync(req.RepoRoot, ct);
            var id = FileIssue(issuesDir, req, $"wrap-up blocked: merge conflict on {req.Branch}", merge.Error ?? "merge conflict");
            return new WrapUpOutcome(WrapUpStatus.KeptConflict,
                $"merge conflict for {req.Prefix}; worktree kept, issue {id} filed.", id);
        }

        // 4. Clean up.
        if (policy.RemoveWorktreeOnMerge)
        {
            await _git.RemoveWorktreeAsync(req.RepoRoot, req.WorktreePath, ct);
            await _git.DeleteBranchAsync(req.RepoRoot, req.Branch, force: false, ct);
        }
        return new WrapUpOutcome(WrapUpStatus.Merged,
            $"{req.Prefix} merged into {policy.MainBranch} and cleaned up.", null);
    }

    private static string FileIssue(string issuesDir, WrapUpRequest req, string title, string detail)
        => IssueStore.Write(issuesDir, $"wt-{req.Prefix.TrimEnd('-')}", title, detail, "high", DateTimeOffset.Now).Id;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~WrapUpServiceTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/WrapUpService.cs tests/Styloagent.Core.Tests/WrapUpServiceTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): gated wrap-up state machine (merge on green, keep+issue on failure)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: `wrap_up` MCP tool + controller wiring

**Files:**
- Modify: `src/Styloagent.Core/Mcp/IFleetController.cs`, `src/Styloagent.App/Mcp/FleetTools.cs`,
  `src/Styloagent.App/Mcp/FleetController.cs`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`,
  `src/Styloagent.Core/Projects/DefaultTemplates.cs`
- Modify (fakes): `tests/Styloagent.App.Tests/FleetToolsTests.cs`, `tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs`
- Test: `tests/Styloagent.App.Tests/FleetToolsTests.cs`

**Interfaces:**
- Consumes: `WrapUpService`, `WrapUpRequest`, `WrapUpOutcome`, `GitPolicyReader` (Tasks 4/9), `IGitService`, `ITestRunner`.
- Produces:
  - `IFleetController.WrapUpAsync(string callerPrefix)` → `Task<WrapUpOutcome>`.
  - `FleetTools.wrap_up()` MCP tool.
  - `MainWindowViewModel.WrapUp(string callerPrefix)` → `WrapUpOutcome`.

- [ ] **Step 1: Write the failing test** — add to `tests/Styloagent.App.Tests/FleetToolsTests.cs` (and update the fake):

Update the `FakeController` in that file to implement the new method:

```csharp
        public string? LastWrapUp;
        public WrapUpOutcome NextWrapUp = new(WrapUpStatus.Merged, "merged foss-", null);
        public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix) { LastWrapUp = callerPrefix; return Task.FromResult(NextWrapUp); }
```

(Add `using Styloagent.Core.Git;` to the test file.) Then the test:

```csharp
    [Fact]
    public async Task wrap_up_uses_the_caller_prefix_and_returns_the_message()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.wrap_up();

        Assert.Equal("foss-", ctrl.LastWrapUp);
        Assert.Contains("merged foss-", result);
    }

    [Fact]
    public async Task wrap_up_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.wrap_up();
        Assert.Null(ctrl.LastWrapUp);
        Assert.Contains("unauthorized", result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~wrap_up"`
Expected: FAIL — `IFleetController.WrapUpAsync` / `FleetTools.wrap_up` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Styloagent.Core/Mcp/IFleetController.cs` — add the method and `using Styloagent.Core.Git;`:

```csharp
using Styloagent.Core.Git;

namespace Styloagent.Core.Mcp;

public interface IFleetController
{
    Task<SpawnOutcome> SpawnAsync(SpawnRequest req);
    FleetSnapshot Snapshot();
    Task<IssueOutcome> ReportIssueAsync(IssueRequest req);
    Task<WrapUpOutcome> WrapUpAsync(string callerPrefix);
}
```

`src/Styloagent.App/Mcp/FleetTools.cs` — add the tool inside the `#pragma warning disable CA1707` region (add `using Styloagent.Core.Git;` at the top):

```csharp
    [McpServerTool, Description("Signal you have finished your work in your worktree. Styloagent will guard-clean, run the project's tests, merge your branch to main and remove the worktree — or, on failure, keep your worktree and file an issue. Only call when your branch is committed and the work is complete.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> wrap_up()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.WrapUpAsync(caller);
        return outcome.Message;
    }
```

`src/Styloagent.App/Mcp/FleetController.cs` — add the marshalled method:

```csharp
    public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.WrapUp(callerPrefix)).GetTask();
```

(add `using Styloagent.Core.Git;`).

`src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — add the VM method. It resolves the agent's worktree/branch, refuses if the agent has no worktree, builds the service from `_git` + a `ProcessTestRunner`, runs it, and refreshes Issues + retires the pane on merge:

```csharp
    /// <summary>
    /// Runs the gated wrap-up for the agent identified by <paramref name="callerPrefix"/>. Requires an
    /// active project and that the agent was spawned with a worktree. Runs on the UI thread.
    /// </summary>
    public WrapUpOutcome WrapUp(string callerPrefix)
    {
        if (_project is null) return new WrapUpOutcome(WrapUpStatus.KeptUncommitted, "no active project", null);
        if (_git is null) return new WrapUpOutcome(WrapUpStatus.KeptUncommitted, "git unavailable", null);

        var pane = Panes.FirstOrDefault(p => p.Prefix == callerPrefix);
        if (pane?.WorktreePath is null || pane.WorktreeBranch is null)
            return new WrapUpOutcome(WrapUpStatus.KeptUncommitted,
                $"{callerPrefix} has no worktree to wrap up.", null);

        var policy = GitPolicyReader.Read(_project.GitPolicyPath);
        var svc = new WrapUpService(_git, new Styloagent.Git.ProcessTestRunner());
        var req = new WrapUpRequest(callerPrefix, _project.Root, pane.WorktreePath, pane.WorktreeBranch);

        var outcome = svc.WrapUpAsync(req, policy, _project.IssuesDir).GetAwaiter().GetResult();

        Issues?.Refresh();
        if (outcome.Merged)
        {
            pane.WorktreePath = null;
            pane.WorktreeBranch = null;
        }
        return outcome;
    }
```

(Ensure `using Styloagent.Core.Git;` is present in the VM.)

`src/Styloagent.App/Mcp/StyloagentMcpServerTests.cs` fake and `src/Styloagent.App/Mcp` — update the OTHER fake controller in `tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs`:

```csharp
        public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix) => Task.FromResult(new WrapUpOutcome(WrapUpStatus.Merged, "merged", null));
```

(add `using Styloagent.Core.Git;` there too).

Update `src/Styloagent.Core/Projects/DefaultTemplates.cs` — add a `wrap_up` bullet to the tools list:

```
- `wrap_up()` — when your branch is committed and the work is done, call this to hand off: Styloagent
  runs the project's tests, merges your branch to main and removes your worktree, or (on failure) keeps
  the worktree and files an issue for triage. Only agents spawned with a worktree can wrap up.
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~wrap_up"`
Expected: PASS. Then the full App suite: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj` → all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Mcp/IFleetController.cs src/Styloagent.App/Mcp/FleetTools.cs src/Styloagent.App/Mcp/FleetController.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.Core/Projects/DefaultTemplates.cs tests/Styloagent.App.Tests/FleetToolsTests.cs tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): wrap_up MCP tool -> gated auto-merge for the calling agent

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: Roster git status badge

**Files:**
- Modify: `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` (add `GitBadge` + `RefreshGitStatusAsync`)
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` (kick a refresh after spawn/wrap-up)
- Test: `tests/Styloagent.Core.Tests/GitBadgeTests.cs` (pure formatting) — keep UI-free.

**Interfaces:**
- Consumes: `GitStatus` (Task 1).
- Produces: `static string GitBadge.Format(GitStatus? status, bool hasWorktree)` — e.g. `"↑3 ↓0 ✎"`, `"✓"`, `""`.
  (Placed in `Styloagent.Core/Git/GitBadge.cs` so it is unit-testable without Avalonia; the pane exposes a
  `GitBadgeText` string property bound in Plan 2's UI.)

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/GitBadgeTests.cs`:

```csharp
using Styloagent.Core.Git;
using Xunit;

public class GitBadgeTests
{
    [Fact]
    public void No_worktree_has_no_badge()
        => Assert.Equal("", GitBadge.Format(null, hasWorktree: false));

    [Fact]
    public void Clean_worktree_shows_a_tick()
        => Assert.Equal("✓", GitBadge.Format(GitStatus.Clean, hasWorktree: true));

    [Fact]
    public void Ahead_behind_and_dirty_compose()
    {
        var s = new GitStatus(true, 3, 1, false, System.Array.Empty<GitChange>());
        Assert.Equal("↑3 ↓1 ✎", GitBadge.Format(s, hasWorktree: true));
    }

    [Fact]
    public void Conflict_is_flagged()
    {
        var s = new GitStatus(true, 0, 0, true, System.Array.Empty<GitChange>());
        Assert.Contains("⚠", GitBadge.Format(s, hasWorktree: true));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitBadgeTests"`
Expected: FAIL — `GitBadge` does not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Git/GitBadge.cs`:

```csharp
using System.Text;

namespace Styloagent.Core.Git;

/// <summary>Formats a compact one-line git badge for the roster (pure, UI-free).</summary>
public static class GitBadge
{
    public static string Format(GitStatus? status, bool hasWorktree)
    {
        if (!hasWorktree || status is null) return "";
        if (status.HasConflicts) return "⚠ conflict";
        if (!status.IsDirty && status.Ahead == 0 && status.Behind == 0) return "✓";

        var sb = new StringBuilder();
        if (status.Ahead > 0) sb.Append($"↑{status.Ahead} ");
        if (status.Behind > 0) sb.Append($"↓{status.Behind} ");
        if (status.IsDirty) sb.Append('✎');
        return sb.ToString().TrimEnd();
    }
}
```

Add to `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` a bindable text property + refresh (uses `IGitService`, passed in by the VM). Add an observable string and a method:

```csharp
    [ObservableProperty]
    private string _gitBadgeText = "";

    /// <summary>Recomputes the git badge for this pane's worktree (no-op if it has none).</summary>
    public async Task RefreshGitStatusAsync(Styloagent.Core.Git.IGitService git)
    {
        if (WorktreePath is null) { GitBadgeText = ""; return; }
        var status = await git.GetStatusAsync(WorktreePath);
        GitBadgeText = Styloagent.Core.Git.GitBadge.Format(status.Ok ? status.Value : null, hasWorktree: true);
    }
```

In `MainWindowViewModel`, after a worktree is created in `SpawnChild` (and after a `WrapUp`), fire-and-forget a refresh so the badge appears:

```csharp
        if (worktreePath is not null && _git is not null)
            _ = paneVm!.RefreshGitStatusAsync(_git);
```

(place after `CreatePaneForProposed` returns a non-null pane; guard `paneVm`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~GitBadgeTests"`
Expected: PASS (4 tests). Build the App to confirm the VM wiring compiles: `dotnet build src/Styloagent.App/Styloagent.App.csproj` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Git/GitBadge.cs src/Styloagent.App/ViewModels/AgentPaneViewModel.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.Core.Tests/GitBadgeTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(git): per-agent git status badge (ahead/behind/dirty/conflict)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 12: Full-suite green + solution build

**Files:** none (verification task).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build styloagent.sln -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 2: Run the Core, App, and Git test suites**

Run:
```bash
dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --no-build
dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --no-build
dotnet test tests/Styloagent.Git.Tests/Styloagent.Git.Tests.csproj --no-build
```
Expected: all pass (Git integration tests skip cleanly if git is absent).

- [ ] **Step 3: Run the UITests suite (regression guard)**

Run: `dotnet test tests/Styloagent.UITests/Styloagent.UITests.csproj`
Expected: all pass (this is the historically flaky suite; confirm the isolation still holds).

- [ ] **Step 4: Commit (if any incidental fixes were needed)**

```bash
git add -A
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "test(git): full-suite green for worktree lifecycle foundation

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage (Plan 1 scope):**
- `IGitService` over git CLI → Tasks 1, 3. ✓
- Worktree on spawn, overview-decided flag → Tasks 6, 7 (+ prompt rule in Task 6). ✓
- Worktree naming/colour → Task 5 (colour already flows from `PresentationStore.DefaultColorFor` in existing pane construction). ✓
- `.worktrees/` git-ignored via `.git/info/exclude` → Task 7 (`EnsureWorktreesIgnored`). ✓
- Gated wrap-up (guard clean → tests → merge → cleanup; fail → keep + issue) → Task 9. ✓
- `wrap_up` MCP tool + deliberate trigger (not idle) → Task 10. ✓
- `git-policy.yaml` (testCommand, removeWorktreeOnMerge, mainBranch) → Task 4. ✓
- Roster git badges → Task 11. ✓
- Issue-on-failure reuses `IssueStore` → Task 9. ✓
- Testing: parsers/state-machine unit (Tasks 2,4,5,9,11), integration opt-in (Tasks 3,8) → ✓.
- **Deferred to Plan 2 (documented, not gaps):** vendored commit-graph + AvaloniaEdit diff controls, the Git panel view/tab, stage/commit/push/pull/branch/stash operations and their UI, the `Styloagent.Git/THIRD-PARTY.md` attribution, the AvaloniaEdit submodule, and the Avalonia bump to 11.3.18. These belong to the visual-client plan.

**Deviations from the spec (intentional):**
- `WrapUpService` lives in `Styloagent.Core.Git` (pure orchestration over interfaces) rather than `Styloagent.Git` as the spec diagram showed — it has no process code, so Core makes it fully unit-testable. Noted here so a reviewer doesn't flag it as drift.

**Type consistency:** `IGitService` method names/signatures are identical across Tasks 1, 3, 7, 9, 10 fakes. `WrapUpOutcome`/`WrapUpStatus`/`WrapUpRequest` identical in Tasks 9 and 10. `SpawnRequest` 6-arg shape consistent in Tasks 6, 7 and updated call sites. `ProjectConfig` gains exactly one field (`GitPolicyPath`) in Task 4, consumed in Task 10.

**Placeholder scan:** no TBD/TODO; every code step carries complete code. Task 7's test notes a possible `BuildOverviewVmAsync` helper extraction because the existing `FleetSpawnTests` setup is the source of truth — the implementer adapts to whatever that file currently does, passing `gitService`/`repoRoot`.
