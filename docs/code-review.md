# Runtime and Git code review

Review date: 17 October 2026. Scope: the current model-discovery changes, agent startup and message delivery, and the Git operations requested during this session. Git findings below were fixed. OpenCode is now integrated as a native v2 runtime. Follow-up fixes cover legacy delivery, provider filtering, metadata persistence and audited dependencies.

## Review status

All actionable findings from this review are fixed. Delivery into Claude/Codex now reports transport admission explicitly; it cannot independently prove that the model consumed terminal input. OpenCode continues to reconcile native consumption by message ID.

## Runtime findings fixed

| Finding | Resulting change | Verification |
| --- | --- | --- |
| Legacy pending notes drained before successful submission | Reserve pushing notes in a durable inflight file; commit only after accepted submission; restore on deferral, exceptions or cancellation | Failed-send, concurrent-arrival, exclusive-claim and abandoned-claim recovery tests |
| Missing PTYs returned success | Explicit Accepted/Unavailable/Deferred admission results; unavailable direct delivery stays retryable | Missing-session and fallback-queue regression tests |
| Compose timeout authorized overwriting operator input | Timeout returns Deferred without writing; check composition and session identity again under the write gate; periodic idle pumps retry | Compose timeout and accepted retry tests |
| Direct DeepSeek and OpenRouter IDs were mixed | Filter capabilities and picker by endpoint; reject incompatible explicit IDs before launch; use repo-local provider configuration for child spawns and MCP capabilities | Direct/OpenRouter endpoint and MCP repository tests |
| Concurrent revival writes could discard agents or continue after shutdown | Serialize metadata updates, save manifests atomically and wait for pending writes during idempotent disposal | Multi-agent metadata persistence and shutdown test |
| A broadcast failure permanently skipped later recipients | Track admission per recipient; retry failed recipients without repeating successful ones | Failed-recipient regression |
| Unknown runtime names silently became Claude | Explicit OpenCode name/profile plus rejection of unknown runtime requests | Parser regression and compiled UI/MCP paths |
| Native messages depended on terminal readiness and keystrokes | HTTP startup, stable native inbox IDs, urgent interruption and consumption reconciliation | Live C# duplicate delivery and running-shell interrupt tests |
| Terminal closure and backend lifetime were coupled | Own backend and TUI separately; preserve native session database across park/resume | Live terminal detachment and AgentSession history recovery |
| OpenCode permission replies needed a real request identity | Native permission list and reply endpoint | Live model-requested shell approval |
| Ownership enforcement could be loaded incorrectly or run too late | Validate generated v2 plugin activation; invoke existing gate before writes | Live denied write leaves original content unchanged |
| Discovery broadened explicit project model overrides | Preserve existing entries and append newly discovered IDs | Core model discovery regression suite |
| Generic reasoning support was mistaken for an effort enumeration | New OpenRouter discoveries offer default only; OpenCode supplies native variants | Generic discovery and native catalog tests |

The legacy delivery path now retains unaccepted notes and never treats composition timeouts as permission to type. Informational notes stay available for explicit check_inbox rather than starting a new turn. The adapter also depends on an experimental v2 instruction endpoint; see [integration limits](opencode-integration.md#validation-and-limits).

## Git findings fixed

| Finding | Resulting change | Verification |
| --- | --- | --- |
| Push and release controls overflowed the narrow sidebar and sat below file lists | Moved actions to the top, made tag controls fit the width and made the workspace scroll | A headless 300-pixel view invoked Push, Create tag and Push tags against a local bare remote |
| New branches had no useful first-push flow | Establish tracking on origin, or the sole configured remote; report missing or ambiguous remote configuration | Local bare-remote integration test verifies the published branch |
| Writes could overlap or refresh the wrong selected repository | Centralized write execution with a busy guard and captured repository target; ignore stale completion UI updates | Deferred-write regression verifies serialization and repository selection |
| Federated repo labels could disagree with the Git operation target | Resolve shared-checkout operations from the selected agent's repository ancestry | Regression verifies the second overview's repository and its worktree override |
| Watcher refreshes replaced a tag name during editing | Preserve edited values while updating the automatic suggestion | Tag edit regression |
| Partial staging could display the working-tree diff from the staged list | Pass the selected list's staged/unstaged intent into diff loading | Regression checks both sides of one partially staged path |
| Older history reads could overwrite a newer selection or a cleared panel | Version history loads and discard stale completions | Delayed-history regression |
| Git stdout and stderr were drained serially | Drain both concurrently and terminate the subprocess tree on cancellation | Existing Git integrations plus code inspection |
| Superseded loads could dispose a cancellation source still owned by an earlier load | Each load now disposes its own source | Superseded-load regression |

The panel now shows the checkout path, branch, ahead/behind counts and write outcomes. No tag or push was performed against the user's repository or any external remote during verification.

## Validation

The complete regression suite passed 1,172 tests: Core 612, App 386, Git 26 and UI 148. The four opt-in C# live OpenCode tests passed separately. Git/UI tests include actual branch and tag publication to disposable local bare remotes through the narrow 300-pixel panel. The normal suite skips provider-billed live tests. Tests now dispose their cockpit view models before removing temporary channels, and shutdown waits for queued metadata saves.

The OpenCode v2 shell suite passed 17 checks with real DeepSeek calls. Its experimental log endpoint returned only a sync marker for historical replay; snapshot-based recovery passed. See [the implemented integration](opencode-integration.md) and [the sanitized shell evidence](evidence/opencode-shell-results.json).

The build still reports existing Avalonia obsolete API warnings, xUnit analyzer warnings and a UI-test MCP assembly-version warning. No new build error remained in the completed checks.

Normal restore and tests now run with NuGet auditing enabled. The adjacent lucidview SVG project was updated to ImageSharp 4.1.3, Drawing 3.1.3 and Fonts 3.2.0, with its renderer migrated to the drawing-canvas API. This changes three files in that checkout, including the conformance harness project. New pixel tests cover transforms, clipping, gradients, font outlines and PNG output. The badge comparison against resvg improved from baseline MSE 2537 to 662; the renderer remains approximate and does not meet that harness's strict default MSE 100 threshold for the badge.

## Release build follow-up

The v1.13.0 draft's macOS arm64 Release publish exposed a requirement that Debug builds did not enforce: ImageSharp 4 and Drawing 3 require a Six Labors build license. The application now removes that project reference entirely. Cached SVG images and Mermaid PNG export use a shared Svg.Skia renderer on the existing Skia 3 ABI. Linux native assets are explicitly aligned with SkiaSharp 3.119.2. The standalone ImageSharp SVG library in lucidVIEW remains separate from Styloagent's dependency graph.

The Six Labors secret prerequisite has been removed from the release workflow. The v1.13.1 release supersedes the unpublished v1.13.0 draft. Release-mode pixel tests cover transforms, clipping, gradients, fonts, cached local SVG scaling, Mermaid PNG export and invalid scale rejection. Svg.Skia is [MIT licensed](https://github.com/wieslawsoltes/Svg.Skia/blob/master/LICENSE.TXT).

## OpenCode existing-project startup follow-up

The user reported a blank, apparently frozen instance on an existing project. The backend and TUI were alive; a process sample showed the main UI thread still servicing its event loop. A new live test reproduced the blank screen through the real TerminalControl. The shell harness had answered probes but did not exercise this renderer, so its earlier success did not cover this path.

XTerm.NET 1.0.15 enters an unsupported APC parser state on OpenTUI's Kitty graphics query and never exits that state on the string terminator. Every later frame is discarded. A streaming filter now removes unsupported DCS/SOS/PM/APC strings before they reach that parser, including strings and terminators split across PTY chunks. It does not retain unbounded payloads. Supported CSI/OSC commands and text pass through unchanged. Existing read-only history replay behavior is preserved.

The new live UI test failed before this fix with a screen containing only a backslash. After the fix it renders the OpenCode screen, accepts typed input and retains that input after detach/reattach. Eight deterministic regressions cover parser poisoning, all split boundaries and cancelled strings. The running stylomail project was restarted with the fix and its actual desktop screen inspected; OpenCode now displays its session and input box.

## OpenCode auto approval

Every attached OpenCode terminal now launches with `--auto`, including restored sessions. The four provider-backed C# integration tests passed again with that flag. The live adapter test verifies that a requested shell command completes without a manual permission reply while the TUI is attached; it then detaches the TUI and verifies the native HTTP permission reply path and urgent interruption. The ownership test still denies the file mutation before it happens.

## Close/reopen and default-model follow-up

Window close previously relied on a limited lifetime event and fire-and-forget cleanup. Forced desktop shutdown could bypass cancellable closing, floating documents could keep the process alive, and pending agent starts were untracked. The cockpit now waits for starts and owned agents to stop; native disposal interrupts session execution before terminating the TUI and server. Main-window close controls application lifetime, OS Quit follows the same path and early application exit cancels initialization.

The launch helpers also wrote the target repo into the global launchd environment, which could make a later app launch reopen an old project automatically. They now use per-launch `open --env` values.

OpenCode's own global default took precedence over DeepSeek Flash. New and restored sessions now resolve the cockpit's Flash default from the native catalog, respecting explicit choices and provider routing. Four catalog tests cover direct priority, numeric stable OpenRouter version ordering, missing Flash and provider isolation; the live startup test checks new and restored model IDs.

## Terminal scroll follow-up (v1.13.4)

The earlier scroll tests checked row strings and offsets but missed terminal modes lost during reattachment. Both the PTY and OpenCode wrapper retain only a 256 KiB raw replay tail. Rebuilding a fresh engine from that tail can omit the initial alternate-screen and mouse commands, or start inside a control string. OpenCode's later cursor-addressed frames then become ordinary scrollback. A failing regression reproduces the lost modes and missing hidden redraws.

VT parsing now belongs to the PTY session and begins before its reader tasks. Dock views subscribe to changes in that shared bounded state and detach their UI callbacks when removed. OpenCode exposes its underlying PTY as the state identity. Pane replacement neither replays the raw tail nor resets the live parser. Resize and rendering share the parser's gate.

A second failing regression exposed missing native wheel forwarding: returning an unhandled wheel event on the alternate screen only let Avalonia's outer scroll viewer move; it never delivered input to OpenCode. Wheel input now uses the negotiated VT mouse protocol and pointer cell, accumulates fractional trackpad deltas and consumes the event. The full-screen outer scrollbar is disabled. Colored transcript layout tests retain fixed shaped row heights while output continues.

The Release suite passed 1,195 tests. Four live UI/process checks separately cover real OpenCode startup, shutdown, long-session scrolling and pane replacement, and a real shell overflowing its replay before the first view. See [v1.13.4 verification](releases/v1.13.4.md).
