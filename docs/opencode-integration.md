# OpenCode integration for Styloagent

Use OpenCode v2 as a managed backend with an attached terminal client. Send startup prompts, bus messages, interruptions and permission replies through its HTTP API. Keep the terminal for rendering and direct operator input.

The installed Homebrew version was upgraded from 1.18.20 to **2.0.25** on 17 October 2026. The shell suite exercised the actual executable with real DeepSeek Flash completions, isolated configuration and data, disposable Git repositories, a real PTY and a local MCP fixture. Its 17 checks passed. OpenCode is now available in the welcome runtime selector and the toolbar. The C# adapter also has live tests against the real cockpit MCP server, permission replies, urgent shell interruption, ownership enforcement, terminal detachment and session restore.

## Version and process ownership

V2 changes the integration surface. Its default CLI connects to a background service, while a private server can be selected explicitly. The old `opencode attach`, v1 `/session` endpoints, singular provider configuration and Kilo plugin interfaces are unsuitable starting points for this version. The v2 CLI exposes `--server` and native `/api` routes. [CLI lifecycle](https://opencode.ai/v2/docs/cli/), [v2 commands](https://opencode.ai/v2/docs/cli/commands).

Start with **one private backend per Styloagent agent**. This preserves the cockpit's existing per-agent MCP headers and environment, including when two agents share a checkout. A single shared backend could reduce overhead later, but would need a tested way to bind MCP identity to each OpenCode session. A header configured once for a whole server cannot distinguish those agents.

Do not use the user's automatic background service for fleet agents: an already-running service does not acquire a newly spawned client's environment, and terminating an owned agent must not terminate unrelated operator sessions.

The Python shell tests isolate configuration and data. The cockpit adapter retains global configuration and private per-agent data, then checks the effective model catalog, MCP connection and ownership plugin before admitting the first prompt. Credential files are kept outside the repository. Arbitrary inherited plugins still need separate compatibility testing.

## Startup sequence

The tested backend command is:

```sh
opencode serve --hostname 127.0.0.1 --port 0 --stdio
```

1. Resolve the executable, check its version and reject an unsupported API generation.
2. Start the backend in the agent's actual checkout. Pass credentials, a random `OPENCODE_SERVER_PASSWORD`, and `OPENCODE_CONFIG_CONTENT` in the process environment. Keep stdin open while the process is owned.
3. Read the startup JSON line from redirected stdout. The observed shape was `{"url":"http://127.0.0.1:<allocated-port>"}`. Do not select a fixed port or parse a TUI banner.
4. Authenticate with HTTP Basic, username `opencode` and the generated password. The test confirmed unauthenticated session requests return 401.
5. Read `/api/info` and validate the actual version and process identity.
6. Wait for the selected entry in `/api/model` and the required connection in `/api/mcp`. A startup URL alone does not establish model readiness: one probe initially saw an empty model catalog.
7. Establish the event subscription, create or recover the exact session, persist its ID, and then submit its initial input.
8. Attach the terminal client to that exact server and session:

```sh
opencode --auto --server http://127.0.0.1:<allocated-port> --session <session-id> <checkout>
```

Every terminal launch includes `--auto`, as requested. OpenCode describes this as auto-approving permissions that are not explicitly denied; the ownership plugin still runs before file mutations.

The real PTY test replayed `STYLOAGENT_READY` from the selected session. Closing that client left the same backend process alive. Consequently, a TUI exit must update view attachment state independently of agent execution state.

The suite measured about 0.1–0.25 seconds from backend launch to authenticated server info on this machine. These are local startup observations, not a performance guarantee. Bound readiness waits and surface process exits and initialization errors in the pane.

## Session and model selection

The cockpit defaults new and restored sessions to DeepSeek Flash. Direct routing uses `deepseek/deepseek-flash`; configured OpenRouter routing chooses the newest stable advertised Flash version. Explicit model choices take precedence. Missing Flash availability is surfaced as a startup error rather than silently selecting an unrelated native global default.

The tested creation request was:

```http
POST /api/session
Content-Type: application/json

{
  "title": "Styloagent agent",
  "location": {"directory": "/absolute/checkout"},
  "model": {
    "providerID": "deepseek",
    "id": "deepseek-flash",
    "variant": "none"
  }
}
```

The response wraps the session in `data`; retain `data.id` before admitting the first prompt. Each private backend starts in its agent's checkout and uses its native default location. Explicit logical macOS paths can create outside-project locations when OpenCode canonicalizes a symlink differently. The adapter sets PWD consistently and leaves location resolution to the backend. A live test now runs two private backends in separate real Git worktrees and verifies their writes stay isolated.

Use `/api/model` as the runtime's capability source. The installed runtime advertised DeepSeek Flash variants `none`, `low`, `high`, and `max`, and a context limit of 1,000,000 tokens. Do not manufacture a `medium` or `xhigh` variant from another CLI's effort list. Filter for enabled models with text output and tool support; retain provider ID, model ID, variant IDs and token limits as separate fields.

Direct DeepSeek worked using the existing key from `~/.styloagent/deepseek.env`, passed to the child as `DEEPSEEK_API_KEY`. OpenRouter model references would use provider `openrouter` and model ID `deepseek/deepseek-v4.1-flash-1017`. Keep OpenRouter's native provider implementation for its request and reasoning behavior. [Provider configuration](https://opencode.ai/v2/docs/providers).

The live suite used the direct DeepSeek provider. No OpenRouter-funded generation was tested because there was no OpenRouter key available. The earlier public OpenRouter catalog fetch confirmed the dated Flash 1017 ID and capabilities.

## Message admission and delivery

The tested message request was:

```http
POST /api/session/<session-id>/prompt
Content-Type: application/json

{
  "id": "msg_<stable-delivery-id>",
  "text": "Read the assigned bus message and act on it.",
  "delivery": "steer",
  "resume": true
}
```

A 200 response acknowledges admission to the durable inbox. It does not acknowledge that the model has read or acted on the message. Repeating an identical explicit ID returned 200 and left exactly one parked inbox item in the test.

| Styloagent action | OpenCode operation | Delivery accounting |
| --- | --- | --- |
| Start an idle agent | Submit a prompt with `resume: true` | Record admission, then consumption separately |
| Steer an active turn | Submit `delivery: "steer"` | Wait for delivery at a boundary; the test received steering after a blocked tool was rejected |
| Park work | Submit `delivery: "queue", resume: false` | Inbox item remains durable without starting an idle execution |
| Promote parked work | Change inbox delivery to `steer` | Treat as a scheduling operation; an exploratory probe started execution |
| Interrupt immediately | Admit the steering input without resuming, then interrupt with `resume=true` | Verify interruption and subsequent consumption as separate transitions |
| Stop without another turn | `POST /api/session/<id>/interrupt?resume=false` | Test returned `interrupted: true` for a running shell tool and `false` when idle |
| Inspect or cancel parked work | GET inbox; DELETE the specific inbox item | Both were verified after a server restart |

The C# adapter test exercises the combined urgent sequence against a running 30-second shell command, confirming interruption and subsequent message consumption. Stable IDs and receipt recovery handle ambiguous admission; explicit fault injection into a lost interruption response remains a future test. [API contract](https://opencode.ai/v2/docs/api).

Create a persistent delivery ledger keyed by `(repository, recipient prefix, bus message path or ID)`. Derive one stable OpenCode message ID from that identity and retry that same ID after an ambiguous response. Track at least `pending`, `admitted`, `consumed`, and `failed`. Record model consumption from native inbox delivery or reconstructed user messages. Never mark a message read merely because bytes were written to a terminal.

Maintain per-recipient receipts for broadcasts. One recipient failing must not permanently suppress the others. Preserve the existing Markdown bus as the audit record; OpenCode's inbox is the execution delivery channel. Informational messages can remain in the bus for explicit `check_inbox`; messages that should wake an agent must be admitted through the runtime adapter.

## Events and reconnect recovery

The test observed native events including:

| Events | Cockpit effect |
| --- | --- |
| `session.execution.started` | Working |
| `session.text.delta` | Transcript presentation, if a native renderer is added |
| `session.tool.called`, `session.tool.progress` | Activity and timeline |
| `permission.asked`, `permission.replied` | Pending approval state tied to the request ID |
| `session.inbox.enqueued`, `session.inbox.delivered` | Admission and consumption receipts |
| `session.usage.updated` | Context and cost telemetry |
| `session.execution.succeeded`, `session.execution.interrupted` | Execution outcome; do not confuse with backend exit |
| `model.updated`, `provider.updated` | Refresh selectable capabilities |

`GET /api/event` is live-only. The official client documentation says subscriptions have neither replay nor automatic reconnection. The .NET adapter therefore needs its own reconnect loop and a bounded event queue; dispatch UI changes onto Avalonia's thread in batches. [Client streaming behavior](https://opencode.ai/v2/docs/build/client/).

**Observed limitation:** on 2.0.25, `GET /api/experimental/session/<id>/log?after=0&follow=false` returned only `log.synced`, without historical business events. Do not base recovery solely on that experimental route.

On reconnect, subscribe and buffer live events, then reload session details, paginated messages, inbox entries, active executions, pending permissions and forms. Reconcile those snapshots before releasing buffered updates. Deduplicate using durable event or message identity when available and re-fetch state if ordering is ambiguous. Session messages and a parked inbox item survived a full backend restart in the shell test, and the resumed model recalled a marker from its previous turn.

## MCP and identity

The tested v2 configuration shape was:

```json
{
  "mcp": {
    "servers": {
      "styloagent": {
        "type": "remote",
        "url": "http://127.0.0.1:<cockpit-port>/mcp",
        "oauth": false,
        "codemode": true,
        "headers": {
          "Authorization": "Bearer {env:STYLOAGENT_MCP_TOKEN}",
          "X-Styloagent-Agent": "<prefix>",
          "X-Styloagent-Repo": "<repository-name>"
        }
      }
    }
  }
}
```

The fixture verified a real model-issued MCP call, environment substitution in its authorization header, and the expected agent and repository headers. The adapter enables Code Mode for fleet tools so native search and execute can reach them. Scoped mode allows execute while nested tools retain their own permission checks. V2 nests server entries under `mcp.servers`; it does not use the old Kilo configuration shape. [MCP configuration](https://opencode.ai/v2/docs/mcp-servers).

The C# live suite also calls list_fleet through the actual StyloagentMcpServer with repo-local test membership. The repository header contains the repository name, matching cockpit membership checks. A full overview-led governed child-spawn UI flow remains outside these tests.

## Permissions and ownership

Use pending request IDs rather than sending `1` and Enter to the terminal. The tested reply endpoint accepts `decision: "once"` or `"reject"`. Both paths were exercised against actual model-requested shell tools. The test also sent a steering message while permission was pending and observed it after rejecting the tool.

Map Prompt, Scoped and Bypass policies to v2's ordered `permissions` rules. V2 uses action `shell`, not v1 `bash`; session creation also accepts explicit permissions. Explicit denies must remain effective when broader allow rules are present. [V2 permission rules](https://opencode.ai/v2/docs/permissions).

The existing ownership gate executes before mutation. An SSE tool notification arrives too late to replace it. The generated local v2 plugin invokes the existing ownership decision before edit/write/patch actions. The existing ownership policy does not gate arbitrary shell writes. The current plugin API has `ctx.permission.hook("evaluate", ...)` and `ctx.tool.hook("execute.before", ...)`; the retired Kilo plugin cannot be copied unchanged. The live C# test verifies a denied write leaves the owned file unchanged. A package directory with package.json and index.ts is used because the installed 2.0.25 loader rejected individual configured file paths. [V2 plugin hooks](https://opencode.ai/v2/docs/build/plugins/).

## Implemented adapter

INativeAgentSession separates structured control from terminal input. OpenCodeSession owns the private backend and attached TUI while fitting the existing IPtySession display interface. AgentSession dispatches startup, park and resume by runtime. Instructions are attached as a durable, replaceable session entry through the tested experimental v2 endpoint; an incompatible endpoint fails startup visibly.

Each agent has persistent data, cache and state storage under LocalApplicationData/Styloagent/opencode, keyed by repository, prefix and checkout. An exclusive lease prevents two cockpits owning the same identity. Global OpenCode configuration is retained; legacy auth.json credentials seed private data storage with user-only file permissions. Environment API keys are inherited, and existing direct DeepSeek/OpenRouter routing tokens are bridged to native provider variables. V2-only OAuth account databases are not imported.

Automated startup, bus messages, urgent interruption and permission replies use HTTP. Only operator keystrokes go to the terminal. Closing the attached TUI leaves the backend running. Parking interrupts execution and stops owned processes while retaining the database. Resume restores the exact session and can change its model and variant. Startup failures appear in the pane's needs-you state.

Bus admission uses a stable ID derived from repository, recipient and message path. A pending receipt is saved before POST; native user-message snapshots establish consumption before marking the cockpit inbox picked up. Repeated admission uses the same ID. If the backend has neither consumed nor queued a recorded input, reconciliation re-admits its durable payload. A fault-injection test covers a process dying between receipt persistence and POST. The coordinator tracks broadcast recipients separately so failed recipients can retry without duplicating accepted ones. Coordinator tracking is in memory; native pending receipts are durable. Existing Claude/Codex delivery still uses their adapters.

SSE wakes snapshot reconciliation; periodic polling recovers missed events. Snapshots drive execution, pending permissions, form notifications, model, variant and context usage. Forms are answered in the terminal. The native catalog supplies provider IDs, exact variants and context limits to agent_capabilities, refreshing on provider/model events and every five minutes. Separate OpenRouter discovery adds future DeepSeek IDs to Claude's routed mode without guessing reasoning variants from capability flags.

## Validation and limits

The 17-check Python shell suite covers the underlying v2 contract. Four C# live tests cover authenticated startup, native discovery, actual cockpit MCP membership, durable instructions, duplicate admission and consumption, recovery before native admission, permission approval, urgent interruption of a running shell command, terminal detachment, model-variant changes on restart, exact history restore through AgentSession park/resume and compaction, abrupt backend termination and restart, separate real Git worktrees, and ownership rejection before mutation. A separate POSIX process test verifies that the backend exits when its parent is killed.

Arbitrary inherited third-party plugins, V2 OAuth credential import, Windows process handling and parent death during a provider generation have not been live-tested. Consumption uses targeted native message-ID lookups so older receipts do not depend on the 100-message telemetry window. The experimental instruction endpoint ties this implementation to the tested v2 contract.

Run the C# live suite explicitly; it uses real provider credits:

```sh
STYLOAGENT_LIVE_OPENCODE=1 dotnet test tests/Styloagent.App.Tests --filter 'FullyQualifiedName~Native_adapter|FullyQualifiedName~Native_ownership|FullyQualifiedName~Agent_session_parks|FullyQualifiedName~Native_backends_write'
```

Run the repeatable shell suite with:

```sh
python3 scripts/test-opencode-integration.py
```

It uses `DEEPSEEK_API_KEY`, or a direct-DeepSeek token from the local routing file, and retains private temporary artifacts. The saved evidence contains outcomes and event counts, without credentials or transcript bodies.

Check abrupt parent-process death without provider calls:

```sh
python3 scripts/test-opencode-parent-death.py
```

## Application shutdown verification

Closing the main cockpit, OS Quit and the shutdown button now converge on awaited agent teardown. Pending starts receive cancellation; native sessions are interrupted before their TUI and backend stop. Floating document windows no longer keep the instance alive after its main cockpit closes. The launch scripts pass project selection with macOS `open --env`, avoiding a persistent project path in the launchd environment.

The live UI lifecycle test starts a real native session shell command with a background `sleep` child, closes MainWindow, and checks backend, terminal, shell and child PIDs have all exited. It uses `/api/session/{sessionID}/shell` directly and makes no provider request. The startup UI test verifies the DeepSeek Flash default on both a new session and a restored session.
