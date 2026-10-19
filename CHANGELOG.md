# Changelog

## [1.13.5](https://github.com/scottgal/styloagent/releases/tag/v1.13.5) — 2026-10-19

- Stop repeated OpenCode “needs input” alerts from null or superseded model errors; report each current idle failure once and clear it when work resumes.
- Keep automatic OpenCode connection recovery out of the human-input queue, and prevent terminal attachment from grabbing keyboard focus.
- Clear resolved Codex approval frames so normal command output cannot raise the same approval again.
- Wait for each new Claude or Claude+DeepSeek terminal to paint before injecting resume or restart prompts.
- Observe checkpoint changes before requesting them, catching fast replies while retaining live sessions on timeout, cancellation or submission failure.

[Full release notes and verification](docs/releases/v1.13.5.md).

## [1.13.4](https://github.com/scottgal/styloagent/releases/tag/v1.13.4) — 2026-10-17

- Preserve terminal state across dock pane and tab replacement, including output received while hidden. Capped raw output replay no longer loses OpenCode's alternate-screen and mouse modes.
- Route wheel and trackpad input to OpenCode using its VT mouse protocol, with the correct pointer cell and accumulated fractional deltas.
- Prevent the outer scroll viewer from shifting a full-screen terminal; retain ordinary transcript scrollback and Ctrl+wheel zoom.
- Add real OpenCode scrolling and pane replacement tests, a real PTY startup-overflow test, and colored diff layout regressions.

[Full release notes and verification](docs/releases/v1.13.4.md).

## [1.13.3](https://github.com/scottgal/styloagent/releases/tag/v1.13.3) — 2026-10-17

- Default new and restored OpenCode sessions to DeepSeek Flash, respecting explicit models and OpenRouter routing.
- Await agent shutdown, cancel pending starts and interrupt native shell commands before closing. Closing the main cockpit also closes floating windows.
- Pass launch-script project paths per process, preventing later launches from inheriting a stale project.
- Always launch the attached OpenCode terminal with `opencode --auto`, including resumed sessions.
- Verify automatic shell approval, explicit permission replies after terminal detachment, and ownership enforcement with real OpenCode/DeepSeek calls.
- Retain the OpenCode startup rendering fix, Six Labors removal and all v1.13.2 changes.

[Full release notes](docs/releases/v1.13.3.md).

## [1.13.2](https://github.com/scottgal/styloagent/releases/tag/v1.13.2) — 2026-10-17

- Fix the blank OpenCode terminal on existing-project startup: unsupported Kitty graphics/control-string probes could permanently trap the terminal parser and swallow every later frame.
- Filter unsupported DCS/SOS/PM/APC strings across arbitrary PTY chunk boundaries while preserving ordinary text, colors and supported terminal commands.
- Add a real OpenCode UI test for late attachment, visible startup, keyboard input and reattachment, plus deterministic control-string regressions.
- Include v1.13.1's removal of Six Labors from application dependencies. Supersede the unpublished v1.13.0 and v1.13.1 drafts.

[Full release notes and verification](docs/releases/v1.13.2.md).

## [1.13.1](https://github.com/scottgal/styloagent/releases/tag/v1.13.1) — 2026-10-17

- Remove Six Labors/ImageSharp from the application dependency graph so Release builds require no license key.
- Keep SVG images, font rendering, gradients and Mermaid PNG export using Svg.Skia and the existing Skia stack.
- Align Linux native Skia assets with the managed package version.
- Supersede the unpublished v1.13.0 draft; retain its OpenCode, DeepSeek and Git improvements.

[Full release notes and verification](docs/releases/v1.13.1.md).

## [1.13.0](https://github.com/scottgal/styloagent/releases/tag/v1.13.0) — 2026-10-17

- Native OpenCode v2 integration: authenticated startup, terminal attachment, durable messages, permissions, urgent interruption, telemetry and session recovery.
- DeepSeek V4.1 Flash 1017 support and automatic model discovery with provider-specific capabilities.
- Working Git push, pull, tag creation and tag publication controls; first-push tracking and correct repository selection.
- Reliable staged diffs, history refresh, Git cancellation, inbox retries, broadcasts and metadata persistence.
- Patched ImageSharp dependencies through a pinned lucidVIEW revision.
- Claude DeepSeek context/model mapping and archiver slug corrections since v1.12.1.

[Full release notes and verification](docs/releases/v1.13.0.md).

## Earlier releases

See the [GitHub release history](https://github.com/scottgal/styloagent/releases).
