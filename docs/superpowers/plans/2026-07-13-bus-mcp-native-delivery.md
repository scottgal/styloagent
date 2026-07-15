# Mission: MCP-native message delivery (bus-)

**Owner:** `bus-` (the coordination / delivery subsystem — bus projection, delivery, MCP verbs).
**Isolation:** you run on your **own worktree** (`agent/bus-`) — you must NOT share cockpit-'s working
tree. Commit there; `wrap_up` when green.

## Why

Delivery today is terminal-injection only: `MessageDeliveryService` (Core) types a nudge into the
recipient's PTY via `PtyMessageInjector`. There is no MCP-native path, so every delivery rides a fragile
"type into the terminal" mechanism — the root under three bugs (ESC-doesn't-break, inject-doesn't-submit,
"check messages" not flowing). The design (`docs/superpowers/specs/2026-07-09-styloagent-cockpit-design.md`
§4.2) says injection should be the **fallback**; for MCP-connected agents the message should be delivered
**through the MCP**. Build that. Issue: `root-message-delivery-is-terminal-injection-only`.

## Hard boundary (coordinate with cockpit-)

**Do NOT touch `src/Styloagent.App/Services/PtyMessageInjector.cs`.** cockpit- is concurrently fixing the
injection *fallback* (ESC-until-killed + reliable submit). You own the **MCP-native primary** path;
injection stays the documented fallback for non-MCP sessions. Agree this contract with cockpit- over the
bus before you change the delivery dispatch.

## Task 1 — DESIGN (gate: review before implementing)

Write `docs/superpowers/specs/2026-07-13-mcp-native-delivery-design.md`. Investigate and choose among:
- (a) an MCP server→client **notification / elicitation** to the recipient's session;
- (b) a `check_inbox()` / `inbox()` verb the **styloagent skill** runs at each turn boundary
  (driven by `UserPromptSubmit`/`Stop` hooks);
- (c) another mechanism the .NET MCP SDK + Claude Code actually support.

Constraints the design MUST honour: injection remains the fallback for non-MCP sessions; **ack = observed
side-effect** (reply/archive lands) is preserved; it interoperates with the priority model
(`urgent/normal/low/info`) and idle-gating; degrade-never-destroy (durable channel files untouched).
Verify the chosen mechanism is real against the .NET MCP SDK (`ModelContextProtocol`) — don't design a
push the SDK can't do.

**Checkpoint: `send_message` overview- for review before writing any implementation code.**

## Task 2+ — IMPLEMENT (after review)

Implement the primary path in `MessageDeliveryService` (Core) + the MCP server
(`src/Styloagent.App/Mcp/FleetTools.cs` / `StyloagentMcpServer`) + the `styloagent` skill as the design
dictates. Keep `PtyMessageInjector` as the untouched fallback. TDD against `MessageDeliveryTests`
(Core.Tests) — assert MCP delivery is used for a connected recipient and injection only as fallback.
Finish `dotnet build` + `dotnet test` green, then `wrap_up`.

## Coordinate

`send_message` overview- (architect/review) and cockpit- (the fallback-contract). **Note:** the delivery
path is currently broken, so the human may relay your messages until cockpit-'s injector fix lands.
`report_issue` anything new. Don't touch `main`; `wrap_up` merges your worktree.
