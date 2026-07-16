# Resume-on-restart — Session Foundation (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On app crash/restart, automatically resume the *active* fleet — each agent that had a live Claude session comes back via `claude --resume <session_id>`, falling back to a context-doc cold-start.

**Architecture:** A crash-safe runtime sidecar (`.styloagent/live-fleet.json`) mirrors the live roster + each agent's last Claude `session_id`, written eagerly + debounced. On startup, agents whose last state was live are recreated and relaunched with `--resume` (staggered, off the UI thread); a resume that dies within ~5s falls back to the existing context-doc cold-start. Parked agents are recreated as ghosts but NOT launched here — lazy wake-on-inbox is Plan B (`bus-`).

**Tech Stack:** .NET 10, Avalonia 11.3, CommunityToolkit.Mvvm, xUnit. Owner: `session-`.

## Global Constraints

- **.NET 10 / Avalonia 11.3**; Native AOT out of scope. macOS-primary.
- **.NET SDK not on PATH** — prefix build/test: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH=$DOTNET_ROOT:$PATH`.
- **Degrade-never-destroy:** a missing/corrupt manifest ⇒ a cold cockpit (today's behaviour), never a crash. Filesystem + git remain the source of truth; the manifest is a rebuildable projection.
- **Presentation-state-is-a-sidecar:** `live-fleet.json` lives under `.styloagent/` (gitignored), never mixed into channel files.
- **No `Process.Start`/PTY spawn on the UI thread** (freeze lesson `cockpit-freeze-git-subprocesses-fork-on-the-ui-t`).
- **No silent failures:** every resume outcome (`resumed | cold-started | skipped | failed`) is logged to the timeline.
- Never hand-write channel files; coordinate over the bus per `.styloagent/PROTOCOL.md`.

---

## File Structure

- `src/Styloagent.Core/Fleet/LiveFleetManifest.cs` — **Create.** Pure record model: `LiveFleetManifest` + `LiveAgentRecord`. No I/O.
- `src/Styloagent.Core/Fleet/LiveFleetManifestStore.cs` — **Create.** Pure(ish) serialize/deserialize + atomic write/read (temp+rename); tolerant reads (missing/corrupt ⇒ empty).
- `src/Styloagent.Core/Fleet/ResumePlan.cs` — **Create.** Pure policy: given a manifest, classify each record → `Resume | Ghost | Skip` (the active/inactive filter). No I/O.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — **Modify.** Persist the manifest on fleet change (debounced); recreate + resume active agents at startup; wire the fallback. Integration points already located: session-id capture context `AgentPaneViewModel.cs:208`, pane creation `CreatePaneForProposed` (~1451), snapshot `BuildFleetSnapshot` (~1240), spawn `SpawnChildAsync` (~1213), `RefreshInstruments` (61).
- `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` — **Modify.** Expose the persisted `SessionId`; add a `ResumeAsync(sessionId)` entry that launches with `--resume` and reports whether the session took (for fallback).
- `src/Styloagent.Core/Sessions/AgentSession.cs` — **Modify (read first).** Add a resume launch that prepends `--resume <id>` to the `claude` args; surface early-exit so the VM can fall back.
- `tests/Styloagent.Core.Tests/LiveFleetManifestTests.cs` — **Create.** Round-trip, atomic write, tolerant read.
- `tests/Styloagent.Core.Tests/ResumePlanTests.cs` — **Create.** The active/inactive classifier.
- `tests/Styloagent.App.Tests/ResumeStartupTests.cs` — **Create.** Seam test with a fake launcher: active → `--resume <id>`; failed resume → cold-start.

---

## Task 1: Spike — validate `claude --resume` on a crash-killed session (DECISION GATE)

**No code ships from this task** — it is research that gates the launch mechanism in Tasks 4-5. Timebox ~1–2h. Record findings in `docs/superpowers/notes/2026-07-16-claude-resume-spike.md` and report to `overview-`.

**Interfaces:**
- Produces: a go/no-go on native `--resume`, and the exact resume invocation (`--resume <id>` vs `--continue`, cwd sensitivity) that Task 4 consumes.

- [ ] **Step 1: Capture a real session id.** Launch `claude` in a scratch dir, do 2–3 turns, note the `session_id` the hooks report (matches what `AgentPaneViewModel.cs:208` stores). Confirm where Claude stores session files (per-cwd vs global; e.g. `~/.claude`).

- [ ] **Step 2: Kill mid-turn.** Start a long turn, `kill -9` the `claude` process (simulate the app crash killing the PTY). Confirm the session file survives on disk.

- [ ] **Step 3: Resume.** From the same cwd, run `claude --resume <session_id>` (and separately `claude --continue`). Record: does it reload the prior context? Behaviour on the interrupted turn? Any prompt/flag needed for non-interactive relaunch under a PTY?

- [ ] **Step 4: Decide + record.** Write the findings note with a clear verdict:
  - **GREEN** → Tasks 4-5 use `--resume <id>` as primary.
  - **RED/uncertain** → the context-doc cold-start (Task 5) becomes primary; `--resume` is dropped or gated. The manifest/tier work (Tasks 2-4 scaffolding) is unaffected.
  - `send_message overview-` with the verdict before proceeding.

---

## Task 2: Live-fleet manifest model + store (pure Core, TDD)

**Files:**
- Create: `src/Styloagent.Core/Fleet/LiveFleetManifest.cs`
- Create: `src/Styloagent.Core/Fleet/LiveFleetManifestStore.cs`
- Test: `tests/Styloagent.Core.Tests/LiveFleetManifestTests.cs`

**Interfaces:**
- Produces:
  - `record LiveAgentRecord(string Prefix, string? ParentPrefix, int Depth, string Responsibility, string ColorHex, string RepoRoot, string? WorktreePath, string? WorktreeBranch, string? LaunchPromptPath, string? SavedContextPath, string? SessionId, string State, string LastUpdated)`
  - `record LiveFleetManifest(IReadOnlyList<LiveAgentRecord> Agents)`
  - `static class LiveFleetManifestStore { static void Write(string path, LiveFleetManifest m); static LiveFleetManifest Read(string path); }`
- Consumes: nothing (Task 1 only informs whether `SessionId` is used downstream; it is persisted regardless).

- [ ] **Step 1: Write the failing round-trip test**

```csharp
using Styloagent.Core.Fleet;
using Xunit;

public class LiveFleetManifestTests
{
    private static LiveAgentRecord Rec(string prefix, string state, string? sid) =>
        new(prefix, "overview-", 1, "resp", "#BA68C8", "/repo", null, null,
            "/repo/.styloagent/launch-prompts/x.md", "/repo/.styloagent/ctx/x.md", sid, state, "2026-07-16T10:00:00Z");

    [Fact]
    public void Round_trips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lf-{Guid.NewGuid():N}.json");
        var m = new LiveFleetManifest(new[] { Rec("cockpit-", "working", "sess-123"), Rec("repo-", "dehydrated", null) });
        LiveFleetManifestStore.Write(path, m);
        var back = LiveFleetManifestStore.Read(path);
        Assert.Equal(2, back.Agents.Count);
        Assert.Equal("sess-123", back.Agents[0].SessionId);
        Assert.Equal("dehydrated", back.Agents[1].State);
        Assert.Null(back.Agents[1].SessionId);
        File.Delete(path);
    }

    [Fact]
    public void Read_of_missing_or_corrupt_file_yields_empty()
    {
        Assert.Empty(LiveFleetManifestStore.Read(Path.Combine(Path.GetTempPath(), "does-not-exist.json")).Agents);
        var bad = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Empty(LiveFleetManifestStore.Read(bad).Agents);   // degrade-never-destroy
        File.Delete(bad);
    }

    [Fact]
    public void Write_is_atomic_no_partial_temp_left_behind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lf-{Guid.NewGuid():N}.json");
        LiveFleetManifestStore.Write(path, new LiveFleetManifest(Array.Empty<LiveAgentRecord>()));
        Assert.False(File.Exists(path + ".tmp"));
        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter FullyQualifiedName~LiveFleetManifest`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement the model**

```csharp
// src/Styloagent.Core/Fleet/LiveFleetManifest.cs
namespace Styloagent.Core.Fleet;

/// <summary>One live/parked agent as the resume path needs it. Pure data; no I/O.</summary>
public sealed record LiveAgentRecord(
    string Prefix, string? ParentPrefix, int Depth, string Responsibility, string ColorHex,
    string RepoRoot, string? WorktreePath, string? WorktreeBranch, string? LaunchPromptPath,
    string? SavedContextPath, string? SessionId, string State, string LastUpdated);

/// <summary>Crash-survivable snapshot of the live roster for resume-on-restart.</summary>
public sealed record LiveFleetManifest(IReadOnlyList<LiveAgentRecord> Agents);
```

- [ ] **Step 4: Implement the store (atomic write, tolerant read)**

```csharp
// src/Styloagent.Core/Fleet/LiveFleetManifestStore.cs
using System.Text.Json;

namespace Styloagent.Core.Fleet;

/// <summary>Serialises the manifest to a sidecar JSON file. Atomic write; never throws on read.</summary>
public static class LiveFleetManifestStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Write(string path, LiveFleetManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Opts));
        File.Move(tmp, path, overwrite: true);   // atomic replace
    }

    /// <summary>Reads the manifest; a missing or corrupt file yields an empty one (degrade-never-destroy).</summary>
    public static LiveFleetManifest Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LiveFleetManifest(Array.Empty<LiveAgentRecord>());
            return JsonSerializer.Deserialize<LiveFleetManifest>(File.ReadAllText(path), Opts)
                   ?? new LiveFleetManifest(Array.Empty<LiveAgentRecord>());
        }
        catch { return new LiveFleetManifest(Array.Empty<LiveAgentRecord>()); }
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter FullyQualifiedName~LiveFleetManifest`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Core/Fleet/LiveFleetManifest.cs src/Styloagent.Core/Fleet/LiveFleetManifestStore.cs tests/Styloagent.Core.Tests/LiveFleetManifestTests.cs
git commit -m "feat(fleet): live-fleet manifest model + atomic tolerant store"
```

---

## Task 3: Resume classifier — active/inactive/skip (pure Core, TDD)

**Files:**
- Create: `src/Styloagent.Core/Fleet/ResumePlan.cs`
- Test: `tests/Styloagent.Core.Tests/ResumePlanTests.cs`

**Interfaces:**
- Consumes: `LiveAgentRecord` (Task 2).
- Produces: `enum ResumeAction { Resume, Ghost, Skip }`, `static ResumeAction ResumePlan.Classify(LiveAgentRecord r, Func<string,bool> worktreeExists)`.
  - `Resume` = active session (`working | idle | needs-you`) with a live worktree (or none).
  - `Ghost` = `dehydrated` (recreate pane, don't launch — Plan B wakes it).
  - `Skip` = `exited`/unknown, or worktree recorded but now missing.

- [ ] **Step 1: Write the failing test**

```csharp
using Styloagent.Core.Fleet;
using Xunit;

public class ResumePlanTests
{
    private static LiveAgentRecord R(string state, string? worktree = null) =>
        new("a-", "overview-", 1, "r", "#fff", "/repo", worktree, null, null, null, "sid", state, "t");

    [Theory]
    [InlineData("working", ResumeAction.Resume)]
    [InlineData("idle", ResumeAction.Resume)]
    [InlineData("needs-you", ResumeAction.Resume)]
    [InlineData("dehydrated", ResumeAction.Ghost)]
    [InlineData("exited", ResumeAction.Skip)]
    [InlineData("unknown", ResumeAction.Skip)]
    public void Classifies_by_state(string state, ResumeAction expected)
        => Assert.Equal(expected, ResumePlan.Classify(R(state), _ => true));

    [Fact]
    public void Active_agent_with_missing_worktree_is_skipped()
        => Assert.Equal(ResumeAction.Skip, ResumePlan.Classify(R("working", "/repo/.worktrees/gone"), _ => false));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter FullyQualifiedName~ResumePlan`
Expected: FAIL — `ResumePlan` undefined.

- [ ] **Step 3: Implement**

```csharp
// src/Styloagent.Core/Fleet/ResumePlan.cs
namespace Styloagent.Core.Fleet;

public enum ResumeAction { Resume, Ghost, Skip }

/// <summary>Pure policy: how each recorded agent comes back on restart. No I/O.</summary>
public static class ResumePlan
{
    public static ResumeAction Classify(LiveAgentRecord r, Func<string, bool> worktreeExists)
    {
        if (r.WorktreePath is { } w && !worktreeExists(w)) return ResumeAction.Skip;
        return r.State switch
        {
            "working" or "idle" or "needs-you" => ResumeAction.Resume,   // live session
            "dehydrated"                        => ResumeAction.Ghost,    // parked; Plan B wakes it
            _                                   => ResumeAction.Skip,     // exited/unknown
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter FullyQualifiedName~ResumePlan`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Fleet/ResumePlan.cs tests/Styloagent.Core.Tests/ResumePlanTests.cs
git commit -m "feat(fleet): resume classifier (active=resume, dehydrated=ghost, else skip)"
```

---

## Task 4: Persist the manifest on every fleet change (App, `session-`)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` (expose `SessionId`)
- Test: `tests/Styloagent.App.Tests/ResumeStartupTests.cs` (persistence half)

**Interfaces:**
- Consumes: `LiveFleetManifest`, `LiveFleetManifestStore` (Task 2); `BuildFleetSnapshot` roster; `AgentPaneViewModel.SessionId`.
- Produces: `MainWindowViewModel.PersistLiveFleet()` (debounced) + a public `BuildLiveFleetManifest()` for the test.

- [ ] **Step 1: Expose the captured session id.** In `AgentPaneViewModel.cs`, add a public read of the existing `_sessionId` field (populated at line 208):

```csharp
/// <summary>The last Claude session id seen on the hook stream (null before first). For resume-on-restart.</summary>
public string? SessionId => _sessionId;
```

- [ ] **Step 2: Write the failing persistence test**

```csharp
// tests/Styloagent.App.Tests/ResumeStartupTests.cs (first test)
using Styloagent.Core.Fleet;
using Xunit;

public class ResumeStartupTests
{
    [Fact]
    public void BuildLiveFleetManifest_captures_prefix_state_and_session_id()
    {
        var vm = MainWindowViewModelTestFactory.WithPanes(   // reuse existing App.Tests harness for a VM w/ panes
            ("overview-", "working", "sess-o"),
            ("cockpit-", "idle", "sess-c"),
            ("repo-", "dehydrated", null));
        var m = vm.BuildLiveFleetManifest();
        Assert.Equal(3, m.Agents.Count);
        Assert.Equal("sess-o", m.Agents.Single(a => a.Prefix == "overview-").SessionId);
        Assert.Equal("dehydrated", m.Agents.Single(a => a.Prefix == "repo-").State);
    }
}
```

> Implementer note: `MainWindowViewModelTestFactory` — reuse the pattern the existing `Styloagent.App.Tests` already uses to build a `MainWindowViewModel` with panes (grep the test project for how panes are seeded in current VM tests; add a tiny helper if none is reusable). Do NOT stand up real PTYs.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter FullyQualifiedName~ResumeStartupTests`
Expected: FAIL — `BuildLiveFleetManifest` undefined.

- [ ] **Step 4: Implement `BuildLiveFleetManifest` + debounced persist.** In `MainWindowViewModel.cs`, mirror `BuildFleetSnapshot` (~1240):

```csharp
public LiveFleetManifest BuildLiveFleetManifest()
{
    var recs = Panes.Select(p => new LiveAgentRecord(
        p.Prefix, p.ParentPrefix, p.Depth, p.Responsibility, p.BorderColorHex,
        _repoRoot ?? _project?.Root ?? "", p.WorktreePath, p.WorktreeBranch,
        p.LaunchPromptPathOrNull(),          // add a small accessor on the pane for its manifest.LaunchPromptPath
        p.SavedContextPathOrNull(),
        p.SessionId,
        p.State == SessionState.Dehydrated ? "dehydrated" : (p.HookStateText ?? "running"),
        DateTimeOffset.UtcNow.ToString("O"))).ToList();
    return new LiveFleetManifest(recs);
}

private System.Threading.Timer? _liveFleetDebounce;
private string LiveFleetPath => Path.Combine(_project!.Root, ".styloagent", "live-fleet.json");

/// <summary>Debounced, crash-safe persist of the live roster for resume-on-restart.</summary>
public void PersistLiveFleet()
{
    if (_project is null) return;
    _liveFleetDebounce ??= new System.Threading.Timer(_ =>
        { try { LiveFleetManifestStore.Write(LiveFleetPath, BuildLiveFleetManifest()); } catch { /* sidecar; never fatal */ } },
        null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    _liveFleetDebounce.Change(1000, System.Threading.Timeout.Infinite);   // ~1s coalesce
}
```

- [ ] **Step 5: Call `PersistLiveFleet()` on every meaningful change.** Add the call at: end of `CreatePaneForProposed` (~1515, after `Panes.Add`), in `RemoveAgentPane`, in `DehydrateAgentByPrefixAsync`/`RehydrateAgentByPrefixAsync` (after `State = ...`), and where `_sessionId` is set (`AgentPaneViewModel.cs:208` → raise an event the VM subscribes to, or call back via the existing `UserInteracted`-style hook) and on hook-state change (near the `RefreshInstruments()` in the hook handler ~518). Keep the manifest read of `BorderColorHex`/accessors non-null-safe.

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter FullyQualifiedName~ResumeStartupTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/ViewModels/AgentPaneViewModel.cs tests/Styloagent.App.Tests/ResumeStartupTests.cs
git commit -m "feat(fleet): persist live-fleet manifest (debounced) on fleet change"
```

---

## Task 5: Startup resume of the active tier + cold-start fallback (App, `session-`)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` (startup resume)
- Modify: `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs` (`ResumeAsync`)
- Modify: `src/Styloagent.Core/Sessions/AgentSession.cs` (read first — `--resume` launch + early-exit signal)
- Test: `tests/Styloagent.App.Tests/ResumeStartupTests.cs` (resume + fallback)

**Interfaces:**
- Consumes: `LiveFleetManifestStore.Read`, `ResumePlan.Classify` (Tasks 2-3); the Task 1 verdict for the launch flag.
- Produces: `MainWindowViewModel.ResumeFleetAsync(LiveFleetManifest, IAgentLauncher)`; `AgentPaneViewModel.ResumeAsync(string sessionId)` returning `bool tookSession`.

- [ ] **Step 1: Read `AgentSession` first.** Confirm how `SpawnAsync` builds the `claude` command/args (the base command + `HookArgs` + `McpArgsFor`). Add a resume launch that prepends `--resume <id>` (verdict-gated from Task 1). Surface an early-exit within ~5s (no valid session) so the caller can fall back. Keep the graceful-dehydrate exit-suppression intact.

- [ ] **Step 2: Write the failing seam tests (fake launcher)**

```csharp
// tests/Styloagent.App.Tests/ResumeStartupTests.cs (add)
[Fact]
public async Task Active_agent_is_relaunched_with_resume_arg()
{
    var launcher = new FakeLauncher();                       // records the args each pane launches with
    var vm = MainWindowViewModelTestFactory.Empty(launcher);
    var m = new LiveFleetManifest(new[]{
        new LiveAgentRecord("cockpit-","overview-",1,"r","#BA68C8","/repo",null,null,null,"/repo/.styloagent/ctx/cockpit.md","sess-c","working","t") });
    await vm.ResumeFleetAsync(m, launcher);
    Assert.Contains("--resume", launcher.ArgsFor("cockpit-"));
    Assert.Contains("sess-c", launcher.ArgsFor("cockpit-"));
}

[Fact]
public async Task Resume_that_dies_fast_falls_back_to_cold_start()
{
    var launcher = new FakeLauncher { FailResumeFor = { "cockpit-" } };   // resumed proc exits <5s
    var vm = MainWindowViewModelTestFactory.Empty(launcher);
    var m = new LiveFleetManifest(new[]{
        new LiveAgentRecord("cockpit-","overview-",1,"r","#BA68C8","/repo",null,null,null,"/repo/.styloagent/ctx/cockpit.md","sess-c","working","t") });
    await vm.ResumeFleetAsync(m, launcher);
    Assert.True(launcher.ColdStarted("cockpit-"));            // fell back to context-doc launch
}

[Fact]
public async Task Dehydrated_agent_is_ghosted_not_launched()
{
    var launcher = new FakeLauncher();
    var vm = MainWindowViewModelTestFactory.Empty(launcher);
    var m = new LiveFleetManifest(new[]{
        new LiveAgentRecord("repo-","overview-",1,"r","#80CBC4","/repo",null,null,null,null,null,"dehydrated","t") });
    await vm.ResumeFleetAsync(m, launcher);
    Assert.False(launcher.Launched("repo-"));                // recreated as a ghost; Plan B wakes it
    Assert.Contains(vm.Panes, p => p.Prefix == "repo-");
}
```

> Implementer note: `FakeLauncher` — extend the existing test launcher/PTY fake in `Styloagent.App.Tests` (grep for the current fake used in spawn tests). Add `ArgsFor`, `FailResumeFor`, `ColdStarted`, `Launched` recorders.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter FullyQualifiedName~ResumeStartupTests`
Expected: FAIL — `ResumeFleetAsync` undefined.

- [ ] **Step 4: Implement `ResumeFleetAsync`.** For each record, `ResumePlan.Classify(...)`:

```csharp
public async Task ResumeFleetAsync(LiveFleetManifest manifest, IAgentLauncher launcher)
{
    foreach (var r in manifest.Agents)
    {
        var action = ResumePlan.Classify(r, Directory.Exists);
        if (action == ResumeAction.Skip) { Timeline.Add(DateTimeOffset.Now, r.Prefix, "not resumed (exited / worktree gone)", r.ColorHex); continue; }

        var pane = RecreatePaneFromRecord(r);   // reuse CreatePaneForProposed plumbing WITHOUT the trailing SpawnAsync
        if (action == ResumeAction.Ghost) { Timeline.Add(DateTimeOffset.Now, r.Prefix, "parked — will wake on message", r.ColorHex); continue; }

        // Resume: try --resume, fall back to cold-start. Stagger to avoid a spawn storm; never fork on the UI thread.
        bool took = r.SessionId is { } sid && await pane.ResumeAsync(sid);
        if (!took) { await pane.SpawnAsync(); Timeline.Add(DateTimeOffset.Now, r.Prefix, "cold-started from context doc", r.ColorHex); }
        else       { Timeline.Add(DateTimeOffset.Now, r.Prefix, "resumed session", r.ColorHex); }
        await Task.Delay(250);   // simple stagger
    }
}
```

Refactor `CreatePaneForProposed` so pane creation and the trailing `_ = paneVm.SpawnAsync()` can be invoked separately (extract a `RecreatePaneFromRecord` that stops before launch). Implement `AgentPaneViewModel.ResumeAsync(sid)` to launch via the session's `--resume` path and return whether the session took (Step 1). Ensure launches run off the UI thread.

- [ ] **Step 5: Call it at startup.** In `InitializeAsync`/`AttachProject`, after the dock factory exists and BEFORE seeding a fresh empty roster, if `live-fleet.json` exists: `await ResumeFleetAsync(LiveFleetManifestStore.Read(LiveFleetPath), _launcher!);` and skip the cold empty-seed when it restored ≥1 pane. Guard with the degrade-never-destroy path (empty manifest ⇒ normal cold start).

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter FullyQualifiedName~ResumeStartupTests`
Expected: PASS.

- [ ] **Step 7: Full build + suite**

Run: `dotnet build Styloagent.sln --nologo && dotnet test Styloagent.sln --nologo --no-build`
Expected: Build 0 errors; all suites green.

- [ ] **Step 8: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/ViewModels/AgentPaneViewModel.cs src/Styloagent.Core/Sessions/AgentSession.cs tests/Styloagent.App.Tests/ResumeStartupTests.cs
git commit -m "feat(fleet): resume active tier on startup with --resume + cold-start fallback"
```

---

## Self-Review

**Spec coverage:** A. manifest → Task 2; write cadence/atomic → Tasks 2,4; B. active-tier startup resume → Task 5; C. lazy wake-on-inbox → **Plan B (bus-)**, explicitly out of this plan; D. fallback → Task 5 (+ Task 1 gate); E. scope/boundaries (overview- resumes, exited skipped, worktree-gone skipped) → Task 3 + Task 5; F. invariants (degrade-never-destroy, sidecar, atomic) → Task 2 tests; G. ownership seam → this is the `session-` half; R1 spike → Task 1 (gates 5); R2 fork-storm/off-UI-thread → Task 5 stagger + note.

**Placeholder scan:** No TBD/TODO. Two explicit "implementer note: reuse existing test harness/fake" pointers remain — these are honest reuse instructions (the App.Tests harness exists), not missing content; the implementer greps the current fake rather than inventing one.

**Type consistency:** `LiveAgentRecord`/`LiveFleetManifest` fields identical across Tasks 2/3/4/5; `ResumeAction`/`Classify` signatures match; `SessionId`, `ResumeAsync(sid)→bool`, `ResumeFleetAsync`, `BuildLiveFleetManifest` names consistent throughout.

## Follow-on: Plan B (`bus-`, lazy wake-on-inbox)

Once Plan A lands: parked agents are already recreated as ghost panes (Task 5). Plan B adds, in the delivery coordinator (`ChannelDeliveryCoordinator`): on startup scan each ghost's inbox (`channel/inbox/<prefix>*.md` + `all-*.md`) and, post-startup, on any new message to a ghost → revive (`ResumeAsync`/cold-start) then deliver idle-gated. Written after Plan A is green.
