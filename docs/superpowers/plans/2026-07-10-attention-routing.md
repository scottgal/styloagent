# Attention Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface agents that need the human — an oldest-first attention queue with a ⚠ badge and an `Alt+→` jump, plus idle-gated auto-reveal that makes a waiting agent's tab visible without ever grabbing keyboard focus.

**Architecture:** A pure Core `AttentionModel` (queue ordering + reveal decision), an App `InteractionMonitor` (input-recency + idle event), and `MainWindowViewModel` wiring that rebuilds the queue on hook events and reveals the head when idle via `SetActiveDockable` only. Human-initiated Jump additionally calls `SetFocusedDockable`.

**Tech Stack:** .NET 10 · Avalonia 11.3 · Dock.Avalonia · CommunityToolkit.Mvvm · xUnit.

## Global Constraints

- Builds on the hook-state channel: `AgentHookState.WaitingForHuman`, `AgentPaneViewModel.NeedsYou`, `OnHookEvent`, `_panesByHookId`, `SelectedPane`, `SetActiveDockable`/`SetFocusedDockable`, `PresentationStore` are all existing.
- **Identity is the prefix** (consistent with the fleet slice). The attention queue holds panes; the pure model works in prefixes.
- **Focus invariant (load-bearing):** auto-reveal calls `SetActiveDockable` ONLY — never `SetFocusedDockable`, never moves keyboard focus. `JumpToNextWaiting` (human action) calls both. Tests must assert the auto path does NOT call `SetFocusedDockable`.
- **Ordering:** oldest-first (`WaitingSince` ascending; null `WaitingSince` last). **Idle window:** 4 seconds. **Hotkey:** `Alt+Right`.
- The repo's `.editorconfig` treats many CA rules as ERRORS — run `dotnet build` and fix every `error CA####`.
- All queue/reveal mutations on the UI thread. No real `claude`/terminal in tests (inject hook events; fake/record the dock factory).
- Commit each task with `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "..."` ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work on `main`; do not branch.

---

### Task 1: Core AttentionModel (queue + reveal decision)

**Files:**
- Create: `src/Styloagent.Core/Attention/AttentionModel.cs`
- Test: `tests/Styloagent.Core.Tests/AttentionModelTests.cs`

**Interfaces:**
- Produces: `sealed record AttentionCandidate(string Id, bool NeedsYou, DateTimeOffset? WaitingSince)`;
  `static class AttentionQueue { IReadOnlyList<string> Build(IEnumerable<AttentionCandidate> candidates); }`;
  `static class AutoReveal { string? Decide(bool humanBusy, string? queueHead, string? activeId); }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/AttentionModelTests.cs`:

```csharp
using Styloagent.Core.Attention;
using Xunit;

namespace Styloagent.Core.Tests;

public class AttentionModelTests
{
    private static AttentionCandidate C(string id, bool needs, int? waitMinutesAgo)
        => new(id, needs, waitMinutesAgo is null ? null : DateTimeOffset.UtcNow.AddMinutes(-waitMinutesAgo.Value));

    [Fact]
    public void Build_orders_waiting_oldest_first_and_excludes_non_waiting()
    {
        var q = AttentionQueue.Build(new[]
        {
            C("young-", true, 1),
            C("working-", false, null),
            C("old-", true, 10),
            C("mid-", true, 5),
        });
        Assert.Equal(new[] { "old-", "mid-", "young-" }, q);   // oldest-first, working- excluded
    }

    [Fact]
    public void Build_puts_null_waiting_since_last()
    {
        var q = AttentionQueue.Build(new[]
        {
            C("nulltime-", true, null),
            C("timed-", true, 3),
        });
        Assert.Equal(new[] { "timed-", "nulltime-" }, q);
    }

    [Fact]
    public void Build_is_empty_when_none_waiting()
        => Assert.Empty(AttentionQueue.Build(new[] { C("a-", false, null), C("b-", false, 2) }));

    [Theory]
    [InlineData(false, "old-", "dash-", "old-")]   // idle, head != active → reveal head
    [InlineData(true,  "old-", "dash-", null)]     // busy → no reveal
    [InlineData(false, "old-", "old-", null)]      // head already active → no reveal
    [InlineData(false, null,   "dash-", null)]     // empty queue → no reveal
    public void Decide_reveals_only_when_idle_and_head_not_active(bool busy, string? head, string? active, string? expected)
        => Assert.Equal(expected, AutoReveal.Decide(busy, head, active));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "AttentionModelTests" --nologo`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

Create `src/Styloagent.Core/Attention/AttentionModel.cs`:

```csharp
namespace Styloagent.Core.Attention;

/// <summary>One agent as the attention router sees it.</summary>
public sealed record AttentionCandidate(string Id, bool NeedsYou, DateTimeOffset? WaitingSince);

/// <summary>Pure ordering of the agents that need the human, oldest-first.</summary>
public static class AttentionQueue
{
    public static IReadOnlyList<string> Build(IEnumerable<AttentionCandidate> candidates)
        => candidates
            .Where(c => c.NeedsYou)
            .OrderBy(c => c.WaitingSince ?? DateTimeOffset.MaxValue)   // nulls last
            .Select(c => c.Id)
            .ToList();
}

/// <summary>Pure decision: which pane (if any) to auto-reveal.</summary>
public static class AutoReveal
{
    public static string? Decide(bool humanBusy, string? queueHead, string? activeId)
        => (!humanBusy && queueHead is not null && queueHead != activeId) ? queueHead : null;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "AttentionModelTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.Core --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Attention/ tests/Styloagent.Core.Tests/AttentionModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(attention): pure AttentionQueue ordering + AutoReveal decision

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: InteractionMonitor (input recency + idle event)

**Files:**
- Create: `src/Styloagent.App/Services/InteractionMonitor.cs`
- Test: `tests/Styloagent.App.Tests/InteractionMonitorTests.cs`

**Interfaces:**
- Produces: `sealed class InteractionMonitor` with ctor `InteractionMonitor(Func<DateTimeOffset>? clock = null)`;
  `void RecordInput()`; `bool IsBusy(TimeSpan window)`. (The `Idle` event/timer is added and wired in Task 4;
  this task delivers the testable recency core.)

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/InteractionMonitorTests.cs`:

```csharp
using Styloagent.App.Services;
using Xunit;

namespace Styloagent.App.Tests;

public class InteractionMonitorTests
{
    [Fact]
    public void IsBusy_is_true_within_the_window_after_input_and_false_after()
    {
        var now = DateTimeOffset.UtcNow;
        var mon = new InteractionMonitor(() => now);
        mon.RecordInput();

        Assert.True(mon.IsBusy(TimeSpan.FromSeconds(4)));   // just typed

        now = now.AddSeconds(5);                             // 5s later, window 4s
        Assert.False(mon.IsBusy(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void IsBusy_is_false_before_any_input()
        => Assert.False(new InteractionMonitor(() => DateTimeOffset.UtcNow).IsBusy(TimeSpan.FromSeconds(4)));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "InteractionMonitorTests" --nologo`
Expected: FAIL — `InteractionMonitor` doesn't exist.

- [ ] **Step 3: Implement**

Create `src/Styloagent.App/Services/InteractionMonitor.cs`:

```csharp
namespace Styloagent.App.Services;

/// <summary>
/// Tracks how recently the human interacted with a terminal, so attention auto-reveal can hold off
/// while they're actively typing. The <c>Idle</c> event is raised by the shell's dispatcher timer in
/// the view model (wired in the attention-reveal task); this type owns only the recency logic.
/// </summary>
public sealed class InteractionMonitor
{
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastInput;

    public InteractionMonitor(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Raised when input has been quiet for the shell's idle window (wired in the reveal task).</summary>
    public event Action? Idle;

    public void RecordInput() => _lastInput = _clock();

    public bool IsBusy(TimeSpan window)
        => _lastInput is { } t && _clock() - t < window;

    /// <summary>Invoked by the shell's idle timer tick; re-raises <see cref="Idle"/> for subscribers.</summary>
    public void RaiseIdle() => Idle?.Invoke();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "InteractionMonitorTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.App --nologo` → fix any `error CA####` (CA1003 may flag the `Action` event — the repo already uses plain events; if CA1003 errors, use `EventHandler` instead and adjust `RaiseIdle`/subscribers, noting the change).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Services/InteractionMonitor.cs tests/Styloagent.App.Tests/InteractionMonitorTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(attention): InteractionMonitor input-recency + idle hook

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Pane WaitingSince + VM attention queue state

**Files:**
- Modify: `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/AttentionQueueVmTests.cs`

**Interfaces:**
- Consumes: `AttentionCandidate`/`AttentionQueue` (Task 1); existing `OnHookEvent`, `_panesByHookId`, `AgentPaneViewModel.{NeedsYou, HookState, Prefix}`.
- Produces on `MainWindowViewModel`: `ObservableCollection<AgentPaneViewModel> AttentionQueue`; `int WaitingCount => AttentionQueue.Count`; `string AttentionHudText`; `void RefreshAttention()`. On `AgentPaneViewModel`: `DateTimeOffset? WaitingSince { get; set; }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/AttentionQueueVmTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.App.Tests;

public class AttentionQueueVmTests
{
    // Drives a hook event through the VM the same way the channel would.
    private static HookEvent Notify(string agentId, string type)
        => new(agentId, "Notification", type);   // (AgentId, EventName, NotificationType) — match the real HookEvent shape

    [Fact]
    public async Task Waiting_agent_enters_the_queue_and_leaving_removes_it()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var hookId = vm.FirstHookIdForTest();          // helper added below (or reuse an existing accessor)
            Assert.Empty(vm.AttentionQueue);

            vm.DispatchHookForTest(Notify(hookId, "permission_prompt"));   // → WaitingForHuman
            Assert.Equal(1, vm.WaitingCount);
            Assert.Contains("waiting", vm.AttentionHudText);

            vm.DispatchHookForTest(Notify(hookId, "idle_prompt"));         // → Idle (not waiting)
            Assert.Empty(vm.AttentionQueue);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

If the VM has no test seam to inject a hook event or read the first hook id, add minimal `internal`
helpers to `MainWindowViewModel`: `internal string FirstHookIdForTest()` (returns the first key of
`_panesByHookId`) and `internal void DispatchHookForTest(HookEvent e) => OnHookEvent(e);`. Use the
REAL `HookEvent` constructor shape (read `src/Styloagent.Core/Hooks/HookEvent.cs`); the `Notify`
helper above is illustrative — match the actual record.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "AttentionQueueVmTests" --nologo`
Expected: FAIL — `AttentionQueue`/`WaitingSince` don't exist.

- [ ] **Step 3: Implement**

On `AgentPaneViewModel`, add:

```csharp
    /// <summary>When this agent entered WaitingForHuman (null when it is not waiting). Drives queue order.</summary>
    public DateTimeOffset? WaitingSince { get; set; }
```

On `MainWindowViewModel`:

```csharp
    public ObservableCollection<AgentPaneViewModel> AttentionQueue { get; } = new();
    public int WaitingCount => AttentionQueue.Count;
    public string AttentionHudText => WaitingCount == 0 ? "" : $"⚠ {WaitingCount} waiting";

    /// <summary>Rebuilds the oldest-first attention queue from the current panes.</summary>
    public void RefreshAttention()
    {
        var order = AttentionModel_Build();
        AttentionQueue.Clear();
        foreach (var prefix in order)
        {
            var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
            if (pane is not null) AttentionQueue.Add(pane);
        }
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(AttentionHudText));
    }

    private IReadOnlyList<string> AttentionModel_Build()
        => Styloagent.Core.Attention.AttentionQueue.Build(
            Panes.Select(p => new Styloagent.Core.Attention.AttentionCandidate(p.Prefix, p.NeedsYou, p.WaitingSince)));
```

(The local method name avoids colliding the Core static `AttentionQueue` with the VM property named
`AttentionQueue`; keep the fully-qualified Core call.)

In `OnHookEvent`, after the existing `pane.HookState = ...` assignment, stamp/clear `WaitingSince` and
refresh. Read the real method; add (using the real `pane`/state variable names):

```csharp
        pane.WaitingSince = pane.NeedsYou ? (pane.WaitingSince ?? DateTimeOffset.UtcNow) : null;
        RefreshAttention();
```

(`NeedsYou` is already derived from `HookState == WaitingForHuman`, so read it AFTER `HookState` is set.
Keep `WaitingSince` stable across repeated waiting events by preserving the existing value.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "AttentionQueueVmTests" --nologo`
Expected: PASS. Then `dotnet test tests/Styloagent.App.Tests --nologo` (no regression). Then
`dotnet build src/Styloagent.App --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/AgentPaneViewModel.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/AttentionQueueVmTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(attention): WaitingSince + oldest-first attention queue in the shell

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Reveal + Jump + idle-gated auto-reveal (focus invariant)

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/AttentionRevealTests.cs`

**Interfaces:**
- Consumes: `AutoReveal.Decide` (Task 1), `InteractionMonitor` (Task 2), `AttentionQueue`/`RefreshAttention` (Task 3), the existing dock activation (`SetActiveDockable`/`SetFocusedDockable`).
- Produces on `MainWindowViewModel`: an injected/owned `InteractionMonitor` (`internal` accessor for tests); `void RevealPane(AgentPaneViewModel pane, bool focus)`; `void AutoRevealHead()`; `[RelayCommand] void JumpToNextWaiting()`; `string? ActivePrefixForTest()`. The auto-reveal path (hook event while idle + the `Idle` event) calls `RevealPane(head, focus:false)`.

**Plan-time verification (do FIRST):** confirm whether `_dockFactory.SetActiveDockable(doc)` moves
keyboard focus in this Dock.Avalonia version. If it does, `RevealPane(pane, focus:false)` must NOT
leave the terminal focused — either capture/restore the previously focused element or skip when a
terminal currently holds focus. The test below asserts the auto path never calls `SetFocusedDockable`;
keep that invariant however the dock behaves. Record findings in the report.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/AttentionRevealTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.App.Tests;

public class AttentionRevealTests
{
    [Fact]
    public async Task Auto_reveal_activates_but_does_not_focus_when_idle()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.UseRecordingDockForTest();                 // swap in a recording dock factory (helper below)
            var hookId = vm.FirstHookIdForTest();

            // idle (no RecordInput) → a waiter should auto-reveal (activate) but NOT focus
            vm.DispatchHookForTest(new HookEvent(hookId, "Notification", "permission_prompt"));

            Assert.True(vm.DockActivatedCountForTest() >= 1, "auto-reveal should activate the tab");
            Assert.Equal(0, vm.DockFocusedCountForTest());  // focus invariant: no keyboard grab
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Busy_human_suppresses_auto_reveal()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.UseRecordingDockForTest();
            vm.InteractionForTest().RecordInput();          // human just typed → busy
            var hookId = vm.FirstHookIdForTest();

            vm.DispatchHookForTest(new HookEvent(hookId, "Notification", "permission_prompt"));

            Assert.Equal(0, vm.DockActivatedCountForTest()); // suppressed while busy
            Assert.Equal(1, vm.WaitingCount);                // still queued
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task JumpToNextWaiting_focuses_the_oldest_waiter()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.UseRecordingDockForTest();
            var hookId = vm.FirstHookIdForTest();
            vm.InteractionForTest().RecordInput();          // busy so nothing auto-reveals
            vm.DispatchHookForTest(new HookEvent(hookId, "Notification", "permission_prompt"));

            vm.JumpToNextWaitingCommand.Execute(null);
            Assert.True(vm.DockFocusedCountForTest() >= 1);  // explicit jump DOES focus
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

The test needs a recording dock seam. Read how `_dockFactory` is typed; add `internal` test helpers
to `MainWindowViewModel`: `internal void UseRecordingDockForTest()` (replaces `_dockFactory` with a
recording wrapper that counts `SetActiveDockable`/`SetFocusedDockable` calls), `internal int
DockActivatedCountForTest()`, `internal int DockFocusedCountForTest()`, `internal InteractionMonitor
InteractionForTest()`, plus `FirstHookIdForTest`/`DispatchHookForTest` from Task 3. If a recording
wrapper is impractical (dock factory not an interface), instead expose `internal int
AutoRevealActivateCount` / `internal int JumpFocusCount` counters incremented inside `RevealPane`
(distinguish `focus` true/false) and assert on those — the invariant is "auto path never focuses",
which those counters capture. Use whichever seam is cleaner; document the choice.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "AttentionRevealTests" --nologo`
Expected: FAIL — reveal/jump/monitor don't exist.

- [ ] **Step 3: Implement**

On `MainWindowViewModel`, add the monitor (create it where the VM is constructed/initialised) and:

```csharp
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(4);
    private readonly InteractionMonitor _interaction = new();
    internal InteractionMonitor Interaction => _interaction;

    /// <summary>Makes a pane's tab visible. <paramref name="focus"/> also grabs keyboard focus
    /// (only for human-initiated jumps — auto-reveal passes false to honour the focus invariant).</summary>
    public void RevealPane(AgentPaneViewModel pane, bool focus)
    {
        var doc = DocumentFor(pane);           // reuse the existing pane→Document lookup (ActivateDocumentFor)
        if (doc is null) return;
        _dockFactory.SetActiveDockable(doc);
        if (focus)
        {
            _dockFactory.SetFocusedDockable(_dockFactory.RootDock, doc);
            SelectedPane = pane;
        }
    }

    /// <summary>Auto-reveal the oldest waiter iff the human is idle and it isn't already active.</summary>
    public void AutoRevealHead()
    {
        var head = AttentionQueue.FirstOrDefault();
        var target = AutoReveal.Decide(_interaction.IsBusy(IdleWindow), head?.Prefix, ActivePrefix());
        if (target is not null && head is not null) RevealPane(head, focus: false);
    }

    [RelayCommand]
    private void JumpToNextWaiting()
    {
        var head = AttentionQueue.FirstOrDefault();
        if (head is not null) RevealPane(head, focus: true);
    }

    private string? ActivePrefix()
        => (_dockFactory?.DocumentDock?.ActiveDockable as Dock.Model.Controls.Document)?.Context is AgentPaneViewModel p ? p.Prefix : null;
```

(Adapt `DocumentFor`/`ActivePrefix`/`RootDock` to the real dock API used elsewhere in the file — the
existing `ActivateDocumentFor` shows the real pane→Document lookup; reuse it. Do NOT invent new dock
plumbing.)

In `OnHookEvent`, after `RefreshAttention()` (Task 3), add: `if (!_interaction.IsBusy(IdleWindow)) AutoRevealHead();`

Wire the idle event once (where the VM subscribes to other events, e.g. after `_hookChannel.EventReceived += ...`):
`_interaction.Idle += () => Avalonia.Threading.Dispatcher.UIThread.Post(AutoRevealHead);`
(The idle *timer* that calls `_interaction.RaiseIdle()` is a small dispatcher timer; add it in the same
place, resetting on `RecordInput` — or, minimally, a `DispatcherTimer` ticking every 1s that calls
`AutoRevealHead()` directly when `!IsBusy`. Keep it simple; the pure decision is already tested.)

Add the test seams: `internal string FirstHookIdForTest()`, `internal void DispatchHookForTest(HookEvent e) => OnHookEvent(e);`,
`internal InteractionMonitor InteractionForTest() => _interaction;`, and the recording-dock or
counter seam chosen in Step 1.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "AttentionRevealTests" --nologo`
Expected: PASS (auto path activates but never focuses; busy suppresses; jump focuses). Then full
`dotnet test tests/Styloagent.App.Tests --nologo`. Then `dotnet build src/Styloagent.App --nologo` →
fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/AttentionRevealTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(attention): idle-gated auto-reveal (no focus grab) + Alt-jump focus

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: View integration — input wiring, badge, Jump, pulse, hotkey

**Files:**
- Modify: `src/Styloagent.App/Views/TerminalControl.axaml.cs`, `src/Styloagent.App/Views/AgentsView.axaml`, `src/Styloagent.App/Views/MainWindow.axaml`, `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs`
- Test: `tests/Styloagent.UITests/AttentionHudTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{AttentionHudText, WaitingCount, JumpToNextWaitingCommand, Interaction}` (Tasks 3-4); `InteractionMonitor.RecordInput` (Task 2); `AgentPaneViewModel.NeedsYou`; existing `CountToBoolConverter`.
- Produces: terminal input routed to `InteractionMonitor.RecordInput`; an attention badge + Jump button in the roster header; a pulse on waiting rows; `Alt+Right` bound to Jump.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.UITests/AttentionHudTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class AttentionHudTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AttentionHudTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Attention_badge_and_jump_appear_when_an_agent_waits()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        return _fx.DispatchAsync(async () =>
        {
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 360, Content = view };
                window.Show();

                vm.InteractionForTest().RecordInput();  // busy so no auto-reveal churn during the test
                vm.DispatchHookForTest(new HookEvent(vm.FirstHookIdForTest(), "Notification", "permission_prompt"));
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s.Contains("waiting"));
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => (b.Content?.ToString() ?? "").Contains("Jump"));
                window.Close();
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.UITests --filter "AttentionHudTests" --nologo`
Expected: FAIL — no badge/Jump in `AgentsView`.

- [ ] **Step 3: Implement**

Input wiring — in `src/Styloagent.App/Views/TerminalControl.axaml.cs`, at the top of the existing
`OnKeyDown`, `OnTextInput`, and `OnPointerPressed` handlers, notify the pane's interaction callback.
The cleanest seam: add `public Action? UserInteracted { get; set; }` on `AgentPaneViewModel`, have
`MainWindowViewModel` set each pane's `UserInteracted = _interaction.RecordInput` when it creates the
pane (in `CreatePaneForProposed` and the overview/`AddAgent` pane creation), and have `TerminalControl`
call `(DataContext as AgentPaneViewModel)?.UserInteracted?.Invoke();` in those three handlers. Read the
real `TerminalControl.axaml.cs` + pane-creation sites and match their style.

Roster header — in `src/Styloagent.App/Views/AgentsView.axaml`, next to the fleet HUD, add:

```xml
        <StackPanel Orientation="Horizontal" Spacing="6" VerticalAlignment="Center"
                    IsVisible="{Binding WaitingCount, Converter={x:Static conv:CountToBoolConverter.Instance}}">
          <TextBlock Text="{Binding AttentionHudText}" FontSize="10" Foreground="#E5A05A" VerticalAlignment="Center" />
          <Button Content="Jump ⌥→" FontSize="10" Padding="6,1" Command="{Binding JumpToNextWaitingCommand}" />
        </StackPanel>
```

Pulse — give `NeedsYou` roster rows a subtle emphasis. In `AgentRowTemplate`, bind the identity dot
(`Ellipse`) or the row border to a pulsing style when `NeedsYou`. Minimal, headless-safe approach: add
a `Classes.needsYou="{Binding NeedsYou}"` on the row `Border` and a style
`Style Selector="Border.needsYou"` with a `BorderBrush="#E5A05A"` + a subtle `Animation` (2s, opacity
0.55↔1.0, IterationCount Infinite) on the ⚠ dot. If an `Animation` proves flaky headless, a static
amber border on `.needsYou` is acceptable — the test only asserts the badge/Jump, not the animation.

Hotkey — in `src/Styloagent.App/Views/MainWindow.axaml`, add a window `KeyBinding`:

```xml
  <Window.KeyBindings>
    <KeyBinding Gesture="Alt+Right" Command="{Binding JumpToNextWaitingCommand}" />
  </Window.KeyBindings>
```

(Confirm the `MainWindow` DataContext is `MainWindowViewModel`; if `KeyBindings` needs the command on
the window's DataContext, it already is.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.UITests --filter "AttentionHudTests" --nologo`
Expected: PASS. Then full `dotnet test --nologo` (whole solution green). Then `dotnet build --nologo`
→ fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Views/TerminalControl.axaml.cs src/Styloagent.App/Views/AgentsView.axaml src/Styloagent.App/Views/MainWindow.axaml src/Styloagent.App/ViewModels/AgentPaneViewModel.cs tests/Styloagent.UITests/AttentionHudTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(attention): input wiring + waiting badge + Jump button + Alt-Right hotkey

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes / follow-ups (not this plan)

- Sound / OS toast notifications; per-agent snooze/mute; UI-configurable idle window.
- README demo: an attention-queue screenshot once landed.
- If `SetActiveDockable` is found to move keyboard focus (Task 4 verification), harden `RevealPane(focus:false)` to capture/restore focus.
