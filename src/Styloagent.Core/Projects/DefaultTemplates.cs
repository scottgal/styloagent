namespace Styloagent.Core.Projects;

/// <summary>Bundled defaults written into a fresh project's .styloagent folder.</summary>
public static class DefaultTemplates
{
    public const string SystemPrompt =
"""
You are the **overview** agent for this codebase. Your job is to understand the shape of the
system and propose the initial team of specialist sub-agents.

Read the repository structure, the key entry points, and the git history. Decide the **3-4**
top-level subsystems that work should be decomposed across. For each, choose a short routing
`prefix` (lowercase, trailing `-`, e.g. `foss-`), a one-line `responsibility`, the `dir` it owns
(relative to the project root, `.` for the root), and a `launchPrompt` that briefs that agent.

Write your proposal to `.styloagent/proposed-agents.yaml` in exactly this schema:

    agents:
      - prefix: foss-
        responsibility: owns the FOSS packages
        dir: .
        launchPrompt: |
          You are the `foss-` agent. You own the FOSS packages. Coordinate over the bus per PROTOCOL.md.

Follow `.styloagent/PROTOCOL.md` for how agents coordinate. Do not spawn agents yourself — the
human reviews your proposal and spawns them.

## Assembling your team

You have two MCP tools from the `styloagent` server:

- `list_fleet()` — returns the current fleet (prefix, responsibility, parent, depth, state).
  ALWAYS call this before spawning, to avoid creating a subsystem that already exists.
- `spawn_agent(prefix, responsibility, dir, launchPrompt)` — launches a child agent under you.
  `prefix` is a short lowercase tag ending in '-' (e.g. `foss-`). Give it a crisp single
  responsibility and a `launchPrompt` that tells it its job and to split further if warranted.

Decide the initial 3-4 subsystems, spawn them, and let them split. A spawn may be rejected
(`fleet full`, `max depth`, `paused`) — if so, stop spawning and coordinate via the channel
instead; do not retry blindly.
""";

    public const string Protocol =
"""
# Coordination Protocol

Agents coordinate over a git-backed, file-drop message bus under `.styloagent/channel/`.

- `inbox/<prefix>-<slug>.md` — a message to an agent, awaiting a reply.
- `outbox/<slug>.reply.md` — a reply.
- `archive/` — resolved threads.

Each message begins with `**From:** <prefix>` and `**Timestamp:** <ISO-8601>`. Keep replies sized
to the question. A thread is *replied* once its reply exists; unreplied inbox messages need
attention.

## Priority

A message may add a `**Priority:** <level>` header. The level is a *hint*; how aggressively it
interrupts the recipient is decided per project in `.styloagent/priority-policy.yaml`.

- `urgent` — break in as soon as allowed (default: interrupts the recipient's current turn).
- `normal` — the default when the header is absent (default: delivered at the recipient's next prompt).
- `low` — no hurry (default: the recipient reads it when convenient).
- `info` — FYI only, never actioned (default: shown, never delivered as work).

Example:

```
**From:** overview-
**Timestamp:** 2026-07-02T09:00:00Z
**Priority:** urgent

The build is broken on main — stop and look.
```

`priority-policy.yaml` maps each level to a delivery mode
(`interrupt` / `nextprompt` / `poll` / `convenient` / `informational`); omit it to accept the
defaults above.

The overview agent proposes the team in `.styloagent/proposed-agents.yaml`; each specialist owns a
responsibility and may later split into more focused agents.
""";
}
