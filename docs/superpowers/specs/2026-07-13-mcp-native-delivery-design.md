# MCP-native message delivery — design

**Owner:** `bus-` (coordination / delivery subsystem)
**Status:** DESIGN — awaiting `overview-` review before implementation
**Issue:** `root-message-delivery-is-terminal-injection-only`
**Plan:** `docs/superpowers/plans/2026-07-13-bus-mcp-native-delivery.md`
**Supersedes the delivery mechanism in:** `2026-07-09-styloagent-cockpit-design.md` §4.2
**Date:** 2026-07-17

---

## 1. Problem

Every bus delivery today rides one path: `MessageDeliveryService` (Core) types a formatted nudge into
the recipient's PTY via `PtyMessageInjector` (App). The cockpit design (§4.2) always intended raw stdin
injection to be a **fallback** for sessions *not* connected to the MCP server; for MCP-connected agents
(the common case — Styloagent auto-injects the MCP config on spawn) the message was meant to arrive
*through the MCP*. That primary path was never built. So the fragile "type into the terminal" mechanism
is the *only* mechanism, and it is the root under three delivery bugs (ESC-doesn't-break,
inject-doesn't-submit, "check messages" not flowing).

**Goal:** build the MCP-native primary delivery path, and shrink injection to the one thing it alone can
do. Constraints that MUST hold (from the mission):

- injection stays the documented fallback for non-MCP sessions;
- **ack = observed side-effect** (the reply/archive landing in the durable channel) is preserved;
- interoperates with the priority model (`urgent/normal/low/info`) and idle-gating;
- **degrade-never-destroy** — the durable channel files are untouched; the durable filesystem stays the
  source of truth, the MCP path is a convenience layer over it.

---

## 2. The mechanism is verified — and it forces the design

The mission is explicit: *verify the chosen mechanism is real against the SDK; don't design a push it
can't do.* I verified both halves against primary sources before choosing.

### 2.1 A server→client "pop" is impossible on this transport (verified)

`StyloagentMcpServer` runs an **in-process HTTP MCP server with `WithHttpTransport(o => o.Stateless =
true)`** (`StyloagentMcpServer.cs:42`). The `ModelContextProtocol.AspNetCore` 2.0.0-preview.2 XML docs
for `HttpServerTransportOptions.Stateless` state, verbatim:

> *"Unsolicited server-to-client messages and all server-to-client requests are also unsupported,
> because any responses [have nowhere to go]. Client sampling, elicitation, and roots capabilities are
> also disabled in stateless mode, because the server cannot make requests."*

And the protocol direction is *away* from sessions over HTTP entirely:

> *"Starting with the 2026-07-28 protocol revision, Streamable HTTP no longer supports sessions: the
> revision removed Mcp-Session-Id (SEP-2567)... The default is `true` (stateless)."*

So although the SDK exposes `McpServer.ElicitAsync`, `SampleAsync`, `SendNotificationAsync`,
`SendMessageAsync`, **none of them can be used to push to a recipient here**: (a) the transport is
stateless, so the server holds no per-agent session handle to target; (b) MCP has no way to *address* a
push to "the agent whose prefix is `router-`" out of band; and (c) even where a session exists, MCP's
server→client primitives (elicitation, sampling, `notifications/*`) do not inject a message as a **new
user turn** in the recipient's agent loop — elicitation prompts the *human*, sampling returns a
completion to the *server*, notifications are logging-level. **There is no MCP primitive that "pops a
message into another agent's conversation."** Option (a) from the mission is not viable — not "we chose
not to," but "the SDK and protocol forbid it here."

**Consequence:** MCP-native delivery must be **pull** — a client→server request the recipient makes
(fully supported statelessly) — triggered at the recipient's own turn boundaries. This is mission
option (b).

### 2.2 The pull trigger is real: Claude Code hooks (verified)

The recipient already runs Styloagent-installed Claude Code hooks (`HookSettings.BuildSettingsJson`
wires `SessionStart/UserPromptSubmit/PreToolUse/PostToolUse/Notification/Stop`). Verified against the
current Claude Code hooks docs (field names are load-bearing, so they were confirmed exactly):

- **`UserPromptSubmit`** — emitting `{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit",
  "additionalContext":"…"}}` (exit 0) injects that text into the turn's context. *This repo already
  proves the pattern:* `HookSettings.SessionStartWithHydration` prints exactly this shape for
  `SessionStart` re-hydration.
- **`Stop`** (fires when the agent finishes a turn / is about to go idle) — emitting
  `{"decision":"block","reason":"…"}` (exit 0) **blocks the stop and feeds `reason` back to the model so
  it keeps working with no human prompt.** This is the mechanism that lets an idle-bound agent
  autonomously pick up a message — the reliable replacement for "wait for idle, then type + Enter."
- **`stop_hook_active`** — present in the Stop hook's stdin JSON, `true` while the agent is already
  continuing because of a prior Stop block. Guard on it to avoid an infinite block loop (Claude also
  hard-caps at 8 consecutive blocks; overridable via `CLAUDE_CODE_STOP_HOOK_BLOCK_CAP`).
- Hooks fire the same way in **headless/spawned** sessions.
- **No hook can interrupt a turn mid-generation.** The earliest any hook acts is a turn boundary. The
  *only* way to break into a running turn is terminal ESC — i.e. the injector.

That last point is the crux that defines injection's irreducible role (§4).

---

## 3. Design — deliver at the recipient's turn boundary; the durable channel is the queue

The durable channel (`.styloagent/channel/inbox|outbox|archive/…`) stays exactly as-is and remains the
source of truth. `send_message` still writes the durable trace (unchanged). What changes is **how the
"you have a message" signal reaches a connected recipient**: instead of the broker typing into the
recipient's PTY, the recipient's own turn-boundary hook surfaces it.

### 3.1 Components

```
send_message ──► ChannelMessageWriter ──► durable channel files   (unchanged, source of truth)
                                   │
ChannelDeliveryCoordinator.PumpAsync (unchanged wiring: sees new msg, routes, knows recipient state)
                                   │
                                   ▼
                      MessageDeliveryService.DeliverAsync(msg, recipientId, recipientState)
                                   │  picks the channel by connectivity + state:
                    ┌──────────────┴───────────────────────────────┐
        MCP-native (hook-connected)                     Injection fallback (unchanged)
                    │                                              │
        PendingInbox.Enqueue(recipientId, note)          IMessageInjector.InjectAsync(...)
        (writes a small per-recipient drop file)          (PtyMessageInjector — session-'s domain)
                    │
   recipient's own hook drains it at its next boundary:
     • Stop  hook → {"decision":"block","reason": <pending>}   (pushing: urgent/normal — autonomous)
     • UserPromptSubmit hook → additionalContext <pending>     (surfacing: low/info — never forces)
                    │
        check_inbox() MCP verb ── same drain, agent-initiated / testable
```

### 3.2 `PendingInbox` (new, `Styloagent.Core/Channel/`)

A small, file-backed, per-recipient store of *surfaced-pending* delivery notes. It is **not** the
channel — it is derived delivery-state (rebuildable, disposable), so degrade-never-destroy holds: losing
it never loses a message (the durable channel still has it; worst case a message is re-surfaced —
at-least-once, never at-most-once).

- Lives under the per-run hooks dir (already temp/per-run): `<hooksDir>/deliver/<safeAgentId>.<mode>`
  where `<mode>` ∈ `{push, info}`. Co-located with the hook that drains it, exactly like the existing
  `hydrationFile`.
- **Two files by delivery mode, each drained by exactly one hook** → no cross-hook race:
  - `.push` — messages whose mode forces action (`Interrupt`, `NextPrompt`). Drained by the **Stop
    hook** → `decision:block` + `reason`.
  - `.info` — messages that surface but never force (`Poll`, `Convenient`, `Informational`). Drained by
    the **UserPromptSubmit hook** → `additionalContext`.
- Append/drain race (App appends while the detached hook drains) is handled by an atomic **claim
  rename**: the hook does `mv f f.$$ && cat f.$$ && rm f.$$`; if the App appends after the `mv`, a fresh
  file starts for the next boundary. In-process (`check_inbox`, App writes) a per-recipient lock keeps it
  consistent.
- Note format is the existing `MessageDelivery.FormatNudge` line (`[bus] <priority> "<slug>" from <x> —
  read it: <path>`) — one per line. The hook emits them verbatim; no PTY, no ESC, no submit.

### 3.3 `MessageDeliveryService` — choose the channel

`DeliverAsync` already receives the recipient's live `AgentHookState`. It gains a `PendingInbox`
alongside the injector and routes:

| recipient (hook-connected = state ≠ `Unknown`) | pushing mode (Interrupt/NextPrompt) | surfacing mode (Poll/Convenient/Informational) |
|---|---|---|
| **Working** (a Stop boundary is coming) | `PendingInbox.Enqueue(.push)` → Stop hook delivers at end-of-turn | `PendingInbox.Enqueue(.info)` → next UserPromptSubmit |
| **WaitingForHuman** (turn paused on input) | `PendingInbox.Enqueue(.push)` → delivered at next boundary | `.info` |
| **Idle** (turn already ended — *no hook will fire on its own*) | **Injection fallback** to create a between-turns turn (see §4) | `.info` (surfaces at next human prompt) |
| **Exited** | None | None |
| non-MCP / **Unknown** (hooks not wired) | **Injection fallback** (unchanged) | None (as today) |

The deferral queue that `MessageDeliveryService` maintains today (`_deferred` + `OnRecipientStateChanged`)
is **subsumed by the Stop hook**: "defer until idle, then deliver" becomes "the recipient's Stop hook
delivers when it goes idle." We keep the deferral queue only where injection is still the channel (the
Idle-wake and non-MCP cases), so `OnRecipientStateChangedAsync` stays for the fallback path.

### 3.4 `check_inbox()` — the explicit MCP verb (new, `FleetTools`)

`check_inbox()` returns and drains the caller's `PendingInbox` (both `.push` and `.info`) — the same
drain the hooks perform, exposed as a first-class client→server tool. It gives us:

- a **testable seam** (`MessageDeliveryTests` asserts a connected recipient's message lands in the pull
  store and `check_inbox` returns it; a non-connected recipient still injects);
- an **agent-initiated pull** for a long-running turn that wants to check mid-work (the styloagent skill
  etiquette: "you may `check_inbox()` at any natural pause");
- the drain path a hook *could* use directly if we ever move drain server-side.

`check_inbox` is client→server — fully supported under stateless HTTP; no session needed.

### 3.5 Hook wiring (`HookSettings.BuildSettingsJson`)

The `UserPromptSubmit` and `Stop` hook commands are enriched to also drain the recipient's deliver files.
Both derive the file paths from the existing `hooksDir` + sanitized agent id — no new constructor params.
Shape (POSIX `sh`, no `jq`, mirroring `SessionStartWithHydration`):

- **UserPromptSubmit:** drop the observation event as today; then, if `<hooksDir>/deliver/<id>.info`
  exists, atomically claim it and print
  `{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext": <contents-as-json>}}`.
- **Stop:** drop the observation event; then, if `stop_hook_active` is *not* true and
  `<hooksDir>/deliver/<id>.push` exists, atomically claim it and print `{"decision":"block","reason":
  <contents>}`. If `stop_hook_active` is true, leave `.push` for the next boundary and exit 0 (loop
  guard).

Delivery is thus carried entirely on the hook channel Styloagent already installs — reliable, no typing.

### 3.6 The `styloagent` skill / protocol copy

There is no discrete `SKILL.md` artifact yet; today the "skill" etiquette is the `Protocol` +
`SystemBriefTools` copy in `DefaultTemplates.cs` injected into every agent. Update it to match reality:

- Correct the now-false lines ("Messages other agents send you are delivered straight into this session…
  You never poll a folder") to describe the boundary-delivery model: *messages arrive at your turn
  boundaries via your session hooks; you may also `check_inbox()` at a natural pause.*
- Add `check_inbox()` to the verb list and to the answer-an-inbox workflow (read → reply via
  `send_message` → the reply/archive is the ack).
- Keep the priority table; note that `urgent` to a busy agent is delivered at the **end of its current
  turn** (not mid-turn) unless the project escalates to the injection fallback.

(A formal Claude Code `styloagent` skill artifact, per cockpit-design §4.1, can be a later slice; this
design only needs the protocol/templates copy to be truthful.)

---

## 4. Injection's irreducible role (why the fallback stays, and shrinks)

Hooks can only act *at a turn boundary*. Two things have no boundary to ride, so they remain the
injector's job — and the injector stays entirely session-'s `PtyMessageInjector` (untouched):

1. **Waking an already-Idle interactive session.** When a message arrives and the recipient's turn has
   *already* ended, no Stop hook will fire and no `UserPromptSubmit` will fire until the human types.
   Creating a new user turn in an interactive Claude Code TUI requires stdin — i.e. injection. This is
   the "real between-turns user turn" cockpit-design §4.2 describes, now scoped to *only* this case.
2. **Non-MCP / Unknown sessions** (hooks not wired) — unchanged fallback.

**What the redesign removes from injection:** the two fragile cases that caused the bugs —
mid-turn ESC-break (`urgent`→Working) and wait-for-idle-then-type (`normal`→Working). Both now ride the
recipient's Stop hook. So injection is left with the *least* fragile case (type at an already-idle
prompt, no ESC-break, no interrupt), which is where it already works. This is the concrete sense in which
this "dissolves the ESC-break and inject-no-submit issues for MCP-connected agents."

**Explicit design decision for `overview-` to confirm:** `urgent` to a *working* MCP agent is delivered
at the **end of its current turn** via the Stop hook, **not** by breaking in mid-turn. Reliable, but a
semantic shift from "interrupt the current turn." True mid-generation interruption remains *possible*
only via the ESC injection fallback, which a project's `priority-policy.yaml` could still escalate to.
Recommendation: default `urgent` to Stop-force-continue (reliable); keep ESC-mid-turn as an opt-in
escalation, owned by session-. Flagging because it changes the felt meaning of "urgent."

---

## 5. How each constraint is honoured

- **Injection stays the fallback** — yes; `PtyMessageInjector` is untouched and still the channel for
  Idle-wake + non-MCP. The dispatch decision (which channel) moves into `MessageDeliveryService`, the
  agreed seam.
- **Ack = observed side-effect** — unchanged. `PendingInbox`/`check_inbox` only mark a message
  *surfaced*, never *resolved*. Acknowledgment remains the reply/archive landing in the durable channel,
  which the coordinator/HUD already observe. "Surfaced" ≠ "acked."
- **Priority model + idle-gating** — preserved and made more literal: the priority→`DeliveryMode`
  mapping (`PriorityPolicy`) is unchanged; pushing modes drain via Stop, surfacing modes via
  UserPromptSubmit; the idle-gate *is* the Stop hook (deliver exactly when the agent reaches its
  boundary).
- **Degrade-never-destroy** — the durable channel files are untouched; `PendingInbox` is disposable
  derived state under the per-run hooks dir; if the MCP/hook path degrades, delivery falls back to
  injection and ultimately to the agent reading the durable channel (still there). At-least-once is
  preserved (duplicates possible, loss is not).

---

## 6. Implementation plan (Task 2+, after review)

TDD against `MessageDeliveryTests` (Core.Tests) throughout.

1. **Core — `PendingInbox`** (new): file-backed per-recipient `.push`/`.info` store; `Enqueue`,
   `Drain(recipientId)`, atomic claim-rename; unit-tested in isolation.
2. **Core — `MessageDeliveryService`**: add `PendingInbox` dependency; route by
   `(hook-connected, state, mode)` per §3.3; keep the injection fallback + deferral queue for
   Idle-wake/non-MCP. Tests: connected+Working+urgent → pending `.push` (no injection); connected+Idle →
   injection; Unknown → injection (as today); low/info → `.info`/None.
3. **App/MCP — `check_inbox()`** in `FleetTools`: returns+drains the caller's `PendingInbox`.
   Auth/caller-prefix guard like the other verbs.
4. **Core — `HookSettings.BuildSettingsJson`**: enrich `UserPromptSubmit` + `Stop` commands with the
   deliver-file drains (§3.5); tests on the generated JSON (drain block present, `stop_hook_active`
   guard present, paths derived from hooksDir+id).
5. **App wiring** (`MainWindowViewModel` ~L705): construct `MessageDeliveryService` with a `PendingInbox`
   rooted at the hooks dir; ensure `<hooksDir>/deliver/` exists. **(cross-domain — see §7).**
6. **Skill/protocol copy** (`DefaultTemplates.cs` + `PROTOCOL.md`): §3.6 edits;
   `DefaultTemplatesTests` updated.
7. `dotnet build` + `dotnet test` green → `wrap_up`.

---

## 7. Boundaries & coordination

- **`PtyMessageInjector.cs` — untouched.** session-'s domain (hard boundary). This design only changes
  *which channel is chosen* (`MessageDeliveryService`), which the mission names as the agreed seam. I
  will confirm the dispatch contract with session- via the bus before landing step 2/5.
- **`HookSettings.cs`** (Core/Hooks) — I extend it for the delivery drains (delivery is my domain; the
  mission says the pull path is "driven by UserPromptSubmit/Stop hooks"). Flagging for `overview-` to
  confirm ownership, since HookSettings also builds the observation/hydration hooks.
- **`MainWindowViewModel.cs`** (App) — a large shared file (session-/cockpit- territory). Step 5 is a
  ~2-line constructor/dir-setup edit. I'll either take it with the owner's sign-off or hand the exact
  diff to the owner. Flagging for `overview-`.
- `MessageDeliveryService`, `PendingInbox`, `FleetTools.check_inbox`, `DefaultTemplates`/`PROTOCOL`
  delivery+skill copy — mine.

---

## 8. Open questions for `overview-` (the review gate)

1. **`urgent` semantics** (§4): OK to default `urgent`→working to *end-of-turn* Stop delivery (reliable,
   not mid-turn), with ESC-mid-turn as an opt-in escalation owned by session-? Or must `urgent` keep
   attempting a true mid-turn break by default?
2. **Idle-wake still injects** (§4.1): confirm it's acceptable that waking an already-idle MCP agent
   still uses the injection fallback (there is no hook/MCP way to start a new turn in an idle TUI).
   This is the honest limit of "MCP-native" given a stateless transport.
3. **`HookSettings.cs` ownership** (§7): am I clear to edit it for the delivery drains, or should that
   route through another owner?
4. **`PendingInbox` location**: under the per-run hooks dir (my proposal — clearly disposable) vs. under
   `.styloagent/channel/pending/` (survives restart but nearer the durable channel). I prefer the hooks
   dir for degrade-never-destroy clarity.
5. **Skill artifact**: treat the "styloagent skill" as the protocol/templates copy for now (a real
   Claude Code `SKILL.md` per §4.1 as a later slice)?
