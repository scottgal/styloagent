# Styloagent

A cross-platform desktop **cockpit** for a fleet of long-lived coding agents that
coordinate through a git-backed, file-drop message bus and a worktree-per-responsibility
model. Built with .NET 10 and Avalonia.

Instead of one giant context trying to understand an entire codebase, work is decomposed
across many focused, long-lived specialist agents — each with its own terminal, its own
worktree, and its own running context document. Styloagent is the environment those agents
run *inside*, and the surface a human uses to see and drive them:

- **Dockable terminals** — one per agent, each a first-class pane you can arrange, split,
  float, rename, and colour. Agents **dehydrate** (spin down to a saved context) and
  **rehydrate** (restart from it) as work ebbs and flows.
- **Bus viewer** — the file-drop message channel rendered as live markdown threads, with
  delivery, acknowledgement, and staleness surfaced.
- **Worktree viewer** — what each agent is working on, live from git.
- **System map** — the layer striation and active message paths, drawn as offline mermaid.

The interaction model is **short markdown Q&A over focused documents**: brief questions,
replies sized to the question, anchored to per-specialist context docs that are the durable
unit of knowledge.

## Status

Early foundation. The headless core is built and tested:

- **`Styloagent.Core`** — the fleet-manifest model, YAML persistence (VYaml), channel→manifest
  seeding, and the `AgentSession` spawn → dehydrate → rehydrate state machine.
- **`Styloagent.Terminal`** — a real PTY session over [Porta.Pty](https://github.com/tomlm/Porta.Pty)
  with a verified interactive round-trip, plus a robust file-change watcher.

The design and the implementation plan live under [`docs/superpowers/`](docs/superpowers/).

## Tech stack

.NET 10 · Avalonia 11.3 · [Dock](https://github.com/wieslawsoltes/Dock) · Porta.Pty ·
XTerm.NET · [VYaml](https://github.com/hadashiA/VYaml) · CliWrap · xUnit.

## Building

```bash
dotnet build Styloagent.sln
dotnet test
```

## License

TBD.
