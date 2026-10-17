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

The macOS arm64 Release publish on 17 October exposed a requirement that Debug builds do not enforce: ImageSharp 4 and Drawing 3 require a valid Six Labors build license. No key is present locally or in the Styloagent Actions secrets. The release workflow now reads `SIXLABORS_LICENSE_KEY`, checks for its presence before the platform matrix and keeps the release in draft until all six builds succeed. A valid key is required to complete publication; the successful Debug regression runs above do not establish Release publish success. See [the official requirement](https://docs.sixlabors.com/articles/imagesharp/index.html).
