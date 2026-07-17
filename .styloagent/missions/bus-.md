# bus- — URGENT: Core for bus-viewer Seen-state + open a 2nd federated instance

You own Coordination & Fleet: Core/Channel state model + multi-repo messaging/federation, MCP verbs,
delivery. You run on your own `agent/bus` worktree — commit there, `wrap_up` when green. For full
domain context also read
/Users/scottgalloway/RiderProjects/styloagent/.styloagent/launch-prompts/bus-mission.md. Coordinate
over the bus per .styloagent/PROTOCOL.md. TDD with fakes; provide Core **seams + diffs**, hold live
MainWindowViewModel wiring for cockpit-.

overview- has root-caused two live operator bugs. You own the Core; cockpit- owns the App surface;
overview- relays the seams between you.

## P0 — Bus viewer classifies EVERY message as "needs attention"; no seen/archived transition
Operator: "ALL the messages in the bus say 'needs attention' — IT DOES NOT WORK AT ALL. It needs to
reflect the actual state and be updated when they're seen / archived."

Root cause (Core): `Styloagent.Core/Channel/BusThreadClassifier.Classify` puts a thread in `Attention`
whenever ANY `Inbox` message has `State == New`, and it only leaves `Attention` on `Replied`/`Archived`.
`BusMessageState` today = New / Replied / Archived — there is **no Seen/read state**, so anything the
operator has merely READ (but not replied to) sits in Attention forever. That is the "everything is
needs-attention" bug.

Your Core work (TDD):
1. Extend the message-state model with a **SEEN/read** transition (`BusMessageState` + `BusMessage`),
   and make `BusThreadClassifier` treat seen-but-unreplied threads as **Recent**, not Attention — so
   Attention means "genuinely unhandled inbound" only.
2. `ChannelProjection` must detect + persist **seen / replied / archived** from the channel files —
   define the on-disk representation of "seen" (e.g. a marker/frontmatter/sidecar) and make reload
   reflect it. Verify Replied/Archived detection actually fires today (confirm it isn't silently
   never-set — that alone could cause the stuck-Attention symptom).
3. Expose a **mark-seen / archive** Core API (+ MCP verb if cockpit- needs it) so the App can mark a
   thread/message seen when the operator views it and archive on demand.

Hand overview- + cockpit- the SEAM: the new state value, the mark-seen/archive API signature, and the
on-disk representation. cockpit- builds against a fake until your Core lands.

## P1 — Open stylobot-commercial as a second, INDEPENDENT federated instance
Operator: "I can't spawn a second instance — I need a new instance for stylobot."

Root cause: multi-repo is startup-only + shared-bus — `MainWindowViewModel.AddRepoOverview` adds ONE
overview pane on styloagent's shared bus from `workspace.yaml`. Per fork B, stylobot-commercial
(which has its OWN full `.styloagent/` fleet + channel) must open as an **independent per-repo
instance** (its own `.styloagent/channel/`, its own fleet/router) via a LIVE "open instance" action —
not an extra pane on styloagent's bus.

Your Core work: the per-repo instance/channel model + the repo→channelRoot resolver so a second
instance can be opened live with its own coordinator. This is the live-federation wiring you had
staged behind AttachProject — **overview- is greenlighting it now** (the operator needs it). Hand
cockpit- the seam it wires to its open-instance gesture.

Report BOTH seams on the bus to overview- for relay to cockpit-. P0 first (it's the loudest).
