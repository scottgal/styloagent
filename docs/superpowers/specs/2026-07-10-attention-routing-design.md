# Attention Routing — idle-gated auto-reveal, no focus-stealing

**Status:** Design — pending approval
**Date:** 2026-07-10
**Author:** Styloagent

---

## 1. Goal

With a recursive fleet spawning many agents, the pressing question becomes *which agent needs me
right now*. Attention routing surfaces the agents in `WaitingForHuman` and, **when you are idle**,
auto-reveals the oldest waiter's terminal — while **never grabbing keyboard focus** and never
switching your view mid-typing. An always-visible **attention queue** (oldest-first) with a ⚠ count
badge and a **Jump to next** action (`Alt+→` + a button) lets you route your own attention on demand.

The invariant that defines "no focus-stealing": **automatic reveal only makes a tab visible
(`SetActiveDockable`); it never calls `SetFocusedDockable` and never moves OS keyboard focus.** Only
the human-initiated Jump focuses.

Builds on the hook-state channel (§4.4): `HookStateMachine` already maps notifications to
`AgentHookState.WaitingForHuman`, and `AgentPaneViewModel.NeedsYou` already reflects it.

---

## 2. Scope

**In scope**
- Attention queue: an ordered (oldest-first) collection of the agents in `WaitingForHuman`, a ⚠
  count badge, and a **Jump to next** command (button + `Alt+→` hotkey) that focuses the oldest waiter.
- Idle-gated auto-reveal: when the human is idle, the oldest waiter's tab is made visible
  (`SetActiveDockable` only) and its roster row pulses; while the human is typing, nothing moves.
- `WaitingSince` timestamp per pane; an `InteractionMonitor` tracking last-input recency.

**Out of scope (later)**
- Sound / OS toast notifications; per-agent snooze or mute; a UI-configurable idle window (a ~4s
  constant is fine); multi-monitor placement.

---

## 3. Behaviour & guarantees

- **Ordering:** oldest-first (`WaitingSince` ascending) — the longest-waiting agent is the most urgent.
- **Idle window:** the human is "busy" if any terminal received key/pointer input within the last
  **4 seconds**; otherwise idle.
- **Auto-reveal triggers:** (a) a new waiter arrives while idle; (b) the human becomes idle (input
  quiet for the window) and the queue is non-empty. In both cases the **queue head** is revealed.
- **Auto-reveal is a no-op** when the queue is empty, the human is busy, or the head is already the
  active document — so it never thrashes or loops.
- **Focus invariant:** auto-reveal calls `SetActiveDockable` only. `JumpToNextWaiting` (human action)
  calls `SetActiveDockable` **and** `SetFocusedDockable`.
- **Leaving the queue:** when an agent transitions out of `WaitingForHuman` (it was answered / went
  back to Working / Exited), `WaitingSince` is cleared and it drops out of the queue.

---

## 4. Components / files

**Core (create) — pure, testable:**
- `Attention/AttentionModel.cs`:
  - `sealed record AttentionCandidate(string Id, bool NeedsYou, DateTimeOffset? WaitingSince)`.
  - `static class AttentionQueue { IReadOnlyList<string> Build(IEnumerable<AttentionCandidate> candidates); }`
    — returns the ids of waiting candidates ordered by `WaitingSince` ascending (nulls last, then
    by encounter order); non-waiting candidates excluded.
  - `static class AutoReveal { string? Decide(bool humanBusy, string? queueHead, string? activeId); }`
    — returns `queueHead` when `!humanBusy && queueHead is not null && queueHead != activeId`; else `null`.

**App (create):**
- `Services/InteractionMonitor.cs` — `void RecordInput()` (called on terminal key/pointer input),
  `bool IsBusy(TimeSpan window)` (true if `RecordInput` happened within `window` of now), and an
  `event Action Idle` raised when input has been quiet for the configured window (driven by an
  internal timer reset on each `RecordInput`). Uses an injected clock delegate `Func<DateTimeOffset>`
  for testability (default `() => DateTimeOffset.UtcNow`).

**App (modify):**
- `ViewModels/AgentPaneViewModel.cs` — add `DateTimeOffset? WaitingSince` (init/set); it is set when
  the pane enters `WaitingForHuman` and cleared otherwise. (Keep the existing `NeedsYou`.)
- `ViewModels/MainWindowViewModel.cs` —
  - `ObservableCollection<AgentPaneViewModel> AttentionQueue` (oldest-first) and `int WaitingCount => AttentionQueue.Count`.
  - `void RefreshAttention()` — rebuild the queue from `Panes` via `AttentionQueue.Build`, projecting
    each pane to an `AttentionCandidate(pane.Prefix, pane.NeedsYou, pane.WaitingSince)`, then map the
    id list back to panes; raise `WaitingCount`/`AttentionHudText`.
  - Extend `OnHookEvent`: after setting `HookState`, stamp/clear `pane.WaitingSince`, call
    `RefreshAttention()`, and if `!_interaction.IsBusy(IdleWindow)` call `AutoRevealHead()`.
  - `void AutoRevealHead()` — `var target = AutoReveal.Decide(_interaction.IsBusy(IdleWindow), head?.Prefix, ActivePrefix);`
    if non-null, `RevealPane(headPane, focus:false)`.
  - `[RelayCommand] void JumpToNextWaiting()` — reveal the queue head with `focus:true`.
  - `void RevealPane(AgentPaneViewModel pane, bool focus)` — `SetActiveDockable(doc)`; only when
    `focus` also `SetFocusedDockable(rootDock, doc)` and set `SelectedPane`.
  - Subscribe to `_interaction.Idle` → `AutoRevealHead()` (on the UI thread).
  - `string AttentionHudText => WaitingCount == 0 ? "" : $"⚠ {WaitingCount} waiting";`
- `Views/TerminalControl.axaml.cs` — call `InteractionMonitor.RecordInput()` from the existing
  `OnKeyDown` / `OnTextInput` / `OnPointerPressed` handlers (via an injected callback or a static
  hook the pane wires). The pane already owns the terminal; route input notification to the monitor.
- `Views/AgentsView.axaml` — a ⚠ **N waiting** badge + a **Jump** button (bound to
  `JumpToNextWaitingCommand`, visible when `WaitingCount > 0` via the existing `CountToBoolConverter`)
  next to the fleet HUD; a subtle **pulse** on `NeedsYou` roster rows.
- `Views/MainWindow.axaml` — a window-level `KeyBinding` `Alt+Right` → `JumpToNextWaitingCommand`.

---

## 5. Data flow

```
agent hook → WaitingForHuman → OnHookEvent: pane.HookState set, pane.WaitingSince stamped
  → RefreshAttention() (queue oldest-first, badge ++)
    ├─ human idle → AutoRevealHead() → RevealPane(head, focus:false)  [tab visible, NO keyboard grab, row pulses]
    └─ human busy → nothing moves; waiter sits in queue
InteractionMonitor.Idle (input quiet 4s) → AutoRevealHead()
human presses Alt+→ / clicks Jump → JumpToNextWaiting → RevealPane(head, focus:true)
agent answered → not WaitingForHuman → WaitingSince cleared → RefreshAttention() drops it, badge --
```

---

## 6. Error handling

- `AutoReveal.Decide` returns null on empty queue / busy / head-already-active, so `AutoRevealHead`
  is a safe no-op in those cases (no thrash).
- All queue/reveal mutations occur on the UI thread; the `Idle` event handler marshals through the
  dispatcher.
- **Plan-time verification (load-bearing):** confirm `SetActiveDockable` alone does not move keyboard
  focus in Dock.Avalonia. If it does, capture the currently-focused element before reveal and restore
  it, OR gate `AutoRevealHead` to only fire when no terminal currently holds keyboard focus. The
  focus invariant in §1/§3 must hold regardless.

---

## 7. Testing

- **Core (pure):** `AttentionQueue.Build` orders oldest-first, excludes non-waiting, handles null
  `WaitingSince` (last); `AutoReveal.Decide` returns head only when idle & head≠active, null when
  busy / empty / head==active.
- **`InteractionMonitor`:** `IsBusy(window)` true immediately after `RecordInput`, false after the
  window lapses (driven by the injected clock); `Idle` fires after quiet.
- **VM:** a `WaitingForHuman` hook event enters the pane into `AttentionQueue` with a `WaitingSince`
  and bumps `WaitingCount`; a subsequent non-waiting event removes it; `JumpToNextWaiting` targets the
  oldest waiter; the auto-reveal path calls `SetActiveDockable` but **not** `SetFocusedDockable`
  (asserted via a fake/recording dock factory); a busy monitor suppresses auto-reveal.
- No real `claude`/terminal in tests; hook events injected directly, dock factory faked/recorded.

---

## 8. Resolved decisions

- **Auto-focus policy:** idle-gated auto-reveal (surface the tab when idle; never grab keyboard;
  never switch mid-typing). Explicit Jump focuses.
- **Ordering:** oldest-first (`WaitingSince` ascending).
- **Hotkey:** `Alt+→` for Jump to next waiting.
- **Idle window:** 4 seconds (a constant; not UI-configurable this slice).
- **Focus invariant:** auto-reveal = `SetActiveDockable` only; Jump = `SetActiveDockable` + `SetFocusedDockable`.
