# Mission: fix the cockpit supervision loop (cockpit-)

**Owner:** `cockpit-`  ·  **Repointed from:** the worktree-survives-spawn fix (your Task-1 commit `1b3a0db` +
`stash@{0}` are preserved on `fix/worktree-survives-spawn` for later — leave them).

## Why

We found these by *operating you*: the bugs are all in the human-supervision + coordination paths — the
machinery that makes running a fleet trustworthy. They're your domain (`Styloagent.App`). Fixing them is
now the priority; the worktree fix waits.

## Branch

Create `fix/cockpit-supervision-loop` **off `main`** (clean, separate from the worktree fix). Note: you
share the working directory with `overview-` (spawned `worktree:false`); overview- will stay out of git
while you work. Commit per fix.

## Scope — App-side only, in priority order

**Fix 1 — Terminal pane scroll + prompt visibility (DO THIS FIRST).**
`src/Styloagent.App/Views/AgentPaneView.axaml` (+ the XTerm.NET/custom terminal control).
Add a vertical scrollbar + mouse-wheel scrollback over the VT buffer, and ensure the interactive
prompt / last line stays in view so Claude Code prompts are **visible and clickable**. This restores
our ability to *watch* you. Issues: `docked-agent-terminal-panes-have-no-scrollbar-ca`,
`docked-agent-panes-pending-prompts-unreachable-c`.
→ **CHECKPOINT: after Fix 1, stop and message `overview-`** so we can verify we can watch you before you
continue.

**Fix 2 — Killed agent must go to Exited.**
`src/Styloagent.App/ViewModels/AgentPaneViewModel.cs`. On PTY `Exited` / explicit kill, force
`HookState = AgentHookState.Exited` regardless of hook events — don't rely on the `SessionEnd` hook,
which doesn't fire on a hard kill, leaving killed tabs stuck on ⚠ "needs you". Issue:
`killed-agent-keeps-stale-needs-you-hard-kill-nev`. Test via a headless `AgentPaneViewModel` test.

**Fix 3 — Make the injection *fallback* correct.**
`src/Styloagent.App/Services/PtyMessageInjector.cs`.
(a) For `breakFirst`/urgent, send ESC **repeatedly until the turn is actually killed** (check idle
between presses, bounded retries) — a single ESC doesn't break Claude Code's turn.
(b) Make submit reliable so a delivered message doesn't need a manual Enter (compare the spawn-submit
fix, commit `54ea63b`, and apply the same technique — likely a settle/delay or separate submit).
Issues: `urgent-deliverys-esc-break-doesnt-interrupt-the`, `bus-delivered-messages-inject-but-dont-submit-re`.
Test via `PtyMessageInjectorTests` with a fake PTY capturing writes (assert repeated ESC on break;
assert submit).

## Out of scope (overview- / architecture owns this)

The **root** fix — MCP-native delivery so messages *pop* via the MCP instead of terminal typing
(`root-message-delivery-is-terminal-injection-only`) — is cross-cutting (Core + MCP + the styloagent
skill), a `bus-`/`session-` design task. You are making the App-side **fallback** correct, not building
the MCP-pop path.

## Discipline

TDD where there's a seam (headless VM + `PtyMessageInjector` tests; the pane AXAML is partly
visual — use the UITest/screenshot harness where you can, note what's manual). Commit per fix. Finish
with `dotnet build Styloagent.sln` + `dotnet test` green.

## Coordinate

Report to `overview-` over the bus (`send_message`) when each fix lands or if blocked — **but note the
delivery path you're fixing is currently broken**, so the human may relay your messages until Fix 3
lands. `report_issue` anything new you find. Do not touch `main`; no PR without the human.
