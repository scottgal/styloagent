# Changelog

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
