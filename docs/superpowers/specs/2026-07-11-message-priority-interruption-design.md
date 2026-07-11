# Message Priority & Per-Project Interruption Policy — Design

**Status:** approved (design questions answered 2026-07-11)

## Problem

The file-drop channel treats every message the same: it lands in `inbox/` and the
recipient notices it whenever it next looks. There is no way for a sender to say
"this is urgent, break what you're doing" versus "FYI, read it whenever." And there
is no per-project control over how aggressively messages are allowed to interrupt an
agent mid-task.

## Concept

Two orthogonal ideas:

1. **Priority** — a *semantic level* the sender stamps on a message (`urgent`,
   `normal`, `low`, `info`). It travels in the message file.
2. **Delivery mode** — *how/when* the recipient's runtime nudges the agent. This is
   resolved **per project** by mapping priority level → delivery mode, so the same
   `urgent` message can hard-interrupt in one project and merely queue in a calmer one.

### Priority levels (`MessagePriority`)

`Urgent`, `Normal`, `Low`, `Info`. Absent/unparneable header ⇒ `Normal`.

### Delivery modes (`DeliveryMode`) — the interruption ladder

| Mode | Behavior |
|---|---|
| `Interrupt` | Send **ESC** (`\x1b`) to the recipient's live PTY session to break the current turn, then inject the message prompt immediately. |
| `NextPrompt` | Queue; inject when the agent next reaches idle (`Notification(idle_prompt)` hook) — actioned at the next turn boundary, never mid-turn. |
| `Poll` | Not pushed. The agent is expected to check the channel on its own cadence; runtime may emit a periodic "check inbox" reminder. |
| `Convenient` | Not pushed. Surfaced in the Bus HUD only; the agent reads it when it chooses. |
| `Informational` | Never actioned or injected. Shown as info (badge/log) only. |

### Default policy (shipped)

| Priority | Delivery mode |
|---|---|
| `urgent` | `Interrupt` |
| `normal` | `NextPrompt` |
| `low` | `Convenient` |
| `info` | `Informational` |

Unmarked messages ⇒ `normal` ⇒ `NextPrompt` (approved default: never break a turn
mid-thought, but still actively delivered).

## Message format

New optional header, alongside `**From:**` / `**Timestamp:**`:

```
**From:** overview-
**Timestamp:** 2026-07-02T09:00:00Z
**Priority:** urgent

Body…
```

## Where it lives

- **Core model:** `MessagePriority` enum; `DeliveryMode` enum; `BusMessage.Priority`
  (default `Normal`). `ChannelProjection` parses the `**Priority:**` header
  (case-insensitive; tolerant → `Normal`).
- **Per-project policy:** `.styloagent/priority-policy.yaml` mapping level → mode.
  `PriorityPolicy` record + `[YamlObject]` file class + tolerant reader with the
  shipped default (mirrors `FleetPolicy`). Add `PriorityPolicyPath` to `ProjectConfig`.
- **App delivery executor:** given a new message, its recipient's session, and hook
  state, resolves `policy.ModeFor(message.Priority)` and applies it:
  - `Interrupt` → `IPtySession.WriteAsync("\x1b")` then inject the prompt text.
  - `NextPrompt` → enqueue; on the recipient's `Notification(idle_prompt)` transition
    to idle, inject the prompt.
  - `Poll` / `Convenient` / `Informational` → HUD only, no injection.
- **Protocol doc:** `DefaultTemplates` `PROTOCOL.md` documents the header, levels, and
  default policy so agents emit and understand it.

## Testing

- Core (pure, `Core.Tests`): header parsing (present/absent/garbage → level);
  `PriorityPolicy` default + YAML round-trip + tolerant fallback; `ModeFor` mapping.
- App: delivery executor decisions per mode (Interrupt injects ESC+prompt; NextPrompt
  defers until idle then injects; others inject nothing) against a fake `IPtySession`.

## Out of scope (v1)

- The agent-side "poll" reminder cadence (Poll mode surfaces in HUD like Convenient
  for now; the periodic reminder is a follow-up).
- Sender UI for choosing priority (senders write the header; a compose UI is later).
