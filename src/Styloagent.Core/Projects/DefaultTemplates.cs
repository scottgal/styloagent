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

The overview agent proposes the team in `.styloagent/proposed-agents.yaml`; each specialist owns a
responsibility and may later split into more focused agents.
""";
}
