# Styloagent

A cross-platform desktop **cockpit** for a fleet of long-lived coding agents that
coordinate through a git-backed, file-drop message bus and a worktree-per-responsibility
model. Built with .NET 10 and Avalonia.

Instead of one giant context trying to understand an entire codebase, work is decomposed
across many focused, long-lived specialist agents — each with its own terminal, its own
worktree, and its own running context document. Styloagent is the environment those agents
run *inside*, and the surface a human uses to see and drive them.

## The cockpit

Roster of agents on the left, dockable agent terminals in the centre, and a
`Signal Bus | Documents` panel on the right:

![The Styloagent cockpit](docs/screenshots/cockpit.png)

### Real terminals, in colour

Each pane launches a real `claude` (or any CLI) over a PTY and renders its full-colour TUI —
24-bit truecolor, the 256-colour palette, background highlights, bold and inverse:

![Colour terminal](docs/screenshots/terminal-colour.png)

### Signal Bus — attention-first

The file-drop message channel, grouped so *what needs you* is glanceable: a pinned
**Needs attention** group (unreplied threads), then **Recent**, then **Archive** — each row
with a status glyph (● unreplied · ↩ replied · ▤ archived), colour-coded participants matching
the roster, and relative time:

![Attention-first Signal Bus](docs/screenshots/bus-attention.png)

### Document Library

Repo and channel markdown grouped by source; click a doc to open it as a rendered document
in the centre dock (tile it beside a terminal):

![Document Library](docs/screenshots/doc-library.png)

Documents render with lucidVIEW's presentation — headings, code, lists, and real
[Naiad](https://www.nuget.org/packages/Naiad) diagrams — via the extracted
`Mostlylucid.LucidView.Markdown` control:

![Rendered markdown document](docs/screenshots/markdown-doc.png)

> Every screenshot above is generated **headlessly from the real controls** by the UITest suite
> (`tests/Styloagent.UITests/ReadmeScreenshotTests.cs`) using the
> [`Mostlylucid.Avalonia.UITesting`](https://www.nuget.org/packages/Mostlylucid.Avalonia.UITesting)
> framework — so the README always reflects the actual UI. Run `dotnet test` to refresh them.

## Status

The cockpit shell and its panels are built and tested end-to-end:

- **Terminals** — real PTY sessions over [Porta.Pty](https://github.com/tomlm/Porta.Pty)
  rendered with the XTerm.NET VT engine, full per-cell colour (fg/bg/inverse/bold), typeable,
  hosted as floatable/tabbable Dock documents.
- **Agent roster** — colour-coded, with a live **⚠ needs-you** state badge driven by injected
  Claude Code hooks (§4.4 hook-state channel).
- **Signal Bus** — attention-first threads from the `ChannelProjection`, colour-aligned with the
  roster.
- **Document Library** — repo+channel markdown, opened as rendered documents via
  `Mostlylucid.LucidView.Markdown` (LiveMarkdown.Avalonia + Naiad), extracted from lucidVIEW.
- **`Styloagent.Core`** — fleet manifest, YAML persistence (VYaml), channel→manifest seeding, the
  `AgentSession` spawn → dehydrate → rehydrate state machine, and the pure bus/doc/hook logic.

The design and implementation plans live under [`docs/superpowers/`](docs/superpowers/).

## Tech stack

.NET 10 · Avalonia 11.3 · [Dock](https://github.com/wieslawsoltes/Dock) · Porta.Pty ·
XTerm.NET · [VYaml](https://github.com/hadashiA/VYaml) ·
`Mostlylucid.LucidView.Markdown` (LiveMarkdown.Avalonia + Naiad) ·
[`Mostlylucid.Avalonia.UITesting`](https://www.nuget.org/packages/Mostlylucid.Avalonia.UITesting) · xUnit.

## Building

```bash
dotnet build Styloagent.sln
dotnet test
```

## License

TBD.
