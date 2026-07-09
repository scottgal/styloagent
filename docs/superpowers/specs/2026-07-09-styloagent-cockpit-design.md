# Styloagent — Cockpit Design (Sub-project 1)

**Date:** 2026-07-09
**Status:** Design approved; ready for implementation planning
**Scope:** The "cockpit-first" sub-project — a desktop client that makes the running
multi-agent file-drop bus **visible and operable**, and owns the agent-session
lifecycle. Later sub-projects (orchestration bootstrap / layer-decomposition) are
out of scope here and get their own spec.

---

## 1. What Styloagent is

Styloagent is a cross-platform (macOS-primary) Avalonia desktop **cockpit** for a
fleet of long-lived Claude Code specialist agents that coordinate through a
git-backed, file-drop message bus and a worktree-per-responsibility model.

The coordination system already exists and runs today (see `/tmp/agent-channel`,
governed by `PROTOCOL.md`): ~9 specialist prefixes (`overview-`, `foss-`, `dash-`,
`deploy-`, `prod-`, `mae-`, `edit-`, `wba-`, `caps-`, plus `all-` broadcast),
routed by filename prefix, with per-agent `saved-context/<prefix>-context.md` docs,
`launch-prompts/`, restart prompts, a 10-minute SLA + redirect-to-closest-agent
rule, and an archive lifecycle. Git is already the async bus.

What is missing is a way to **see and drive** it instead of grepping `/tmp` and
babysitting `fswatch` monitors. Styloagent is that client. It is the *generic
perfect client* for this pattern: its core competency is **window / session
lifecycle** — many terminals that dehydrate and rehydrate from focused context,
and split/merge as responsibilities striate and consolidate.

### The bus, reframed (semantics)

Today the channel is **pull-based**: every agent runs fragile `fswatch` monitors
(the "looks alive but emits nothing" PATH gotcha is documented in the PROTOCOL
itself). Styloagent flips this to a **push-broker** model: it *delivers* by
injecting a `check inbox` command into the addressee's terminal, then confirms
delivery by *observing the side effect* (a `.reply.md` lands, or the inbox file is
archived). Delivery is **at-least-once**; **ack = observed side-effect**. The
10-minute SLA, staleness, and redirect-to-closest rules stop being etiquette and
become enforced, visualized broker behavior.

### The interaction model: short markdown Q&A over focused documents

The whole system is **short markdown Q&A anchored to focused documents.** Two units:

- **The focused document** is the durable unit of *knowledge* — each agent's
  `saved-context/<prefix>-context.md`, the coordination spec, the launch/restart
  prompts. Context is decomposed across many small, focused documents (one per
  specialist), not held in one giant context. Documents can live in many places
  (channel dir, repo, Styloagent config) — location is flexible; *focus* is the
  invariant.
- **Short markdown Q&A** is the unit of *interaction* — a brief question → a reply
  sized to the question (the PROTOCOL's "match the shape of the reply"). The bus is
  the transient Q&A layer; the focused documents are what the Q&A reads from and
  writes back to.

This shapes the UI: everything renders as markdown (lucidView); the compose surface
**nudges brevity**, not essays; and dehydrate/rehydrate is exactly "collapse a
session down to its focused document, and rebuild it from that document."

---

## 2. Hard invariant

**The filesystem + git are the durable source of truth. Styloagent is a
projection + delivery + presentation layer over them.**

- Closing Styloagent must not break the channel — agents' own `fswatch` still
  fires. Styloagent must never become a component the system *cannot run without*.
- Styloagent's own additive state (colors, layout, geometry) is presentation-only
  and lives in a sidecar, never mixed into the shared channel files.
- The tool degrades; it never destroys. Every failure mode leaves the durable
  files intact.

---

## 3. Data model

### 3.1 Two kinds of state

- **Fleet manifest (durable, git-friendly, shared with agents).** The binding
  `prefix ↔ repo ↔ worktree ↔ launch-prompt ↔ restart-prompt ↔ saved-context-path
  ↔ transport (local | ssh <host> + credential *reference*)`. YAML via VYaml.
  *Seeded* on first import from what already exists in the channel
  (`launch-prompts/`, `saved-context/`, the `PROTOCOL.md` routing table). Thereafter
  those files remain the source of truth; Styloagent maintains **one thin
  machine-readable index** alongside them (the PROTOCOL routing table is prose and
  must not be continuously re-parsed — seed once, human-confirm).
- **Presentation sidecar (cockpit-only, not shared).** Border colors, display
  names, dock layout, window geometry. Styloagent-owned config. Layout is
  serialized; **live PTY state is never serialized** — restore always means
  re-spawn (which *is* the rehydrate path).

### 3.2 The runtime entity — `AgentSession`

Binds a manifest entry to a PTY. Lifecycle:

```
Unspawned ──spawn──> Live ──dehydrate──> Dehydrated (ghost) ──rehydrate──> Live
```

Holds: the PTY connection (when live), output buffer, per-message delivery state,
and SLA timers. A dehydrated session keeps its dock slot and restart-prompt
reference (the ghost) but owns no process.

### 3.3 Agent identity is the cross-cutting join key

Every agent has one identity (prefix + color). That color propagates everywhere
the agent appears — terminal border, dock tab, roster dot, its messages in the bus
panel, and its node in the System Map. Selecting an agent anywhere highlights it in
the terminal, bus, worktree, and map simultaneously.

---

## 4. Architecture — three isolated engines + a broker

Each engine has one job, no UI, and is testable headless.

1. **Channel projection engine** — watches `/tmp/agent-channel`; parses the
   filename convention + routing table into an in-memory model: **threads** (a
   thread = one shared slug across an inbox message + its `.reply.md` + any
   `follow-up-` messages), **message state** (`new → delivered → read → replied →
   archived`), per-agent queue depth, SLA/staleness clocks. Pure read. Testable
   against a fixture channel directory.

2. **Session / PTY host** — owns `Porta.Pty` connections: spawn / kill / write /
   read; **idle detection** (so injection is safe); **dehydrate** (inject "save
   your context to `saved-context/<prefix>-context.md`" → wait for that file to
   actually change → kill PTY); **rehydrate** (spawn → `cd` → paste
   `launch-prompts/<prefix>-restart.md`). Depends only on Porta.Pty.

3. **Git worktree reader (`IGitReader`)** — enumerate worktrees across repos plus
   status / ahead-behind / log / diff via the git CLI (CliWrap):
   `git worktree list --porcelain`, `git status --porcelain=v2 --branch`,
   `git log --format=…`, `git diff --shortstat`. Watched + debounced. Pure read,
   swappable implementation.

**The broker** wires them together and **hosts the agent-facing MCP server** (§4.1):
projection says "`foss-` has a new inbox message" → the broker waits for `foss-` to
be idle → delivers (§4.2) → watches the projection for the ack → re-nudges or
surfaces the redirect-to-closest action when the SLA breaks.

### 4.1 The agent-facing surface — MCP verbs + skill etiquette

**Styloagent is the environment its agents run inside** — the human operates it, the
agents live in it. So beyond visualizing the bus, it gives each agent first-class
*verbs* instead of the fragile `fswatch` + filename hand-rolling they use today. Two
layers:

**MCP server (the verbs).** Styloagent already owns the three engines, so it *is* the
natural MCP host. Because it also owns the spawn, it **auto-injects the MCP config
when it launches each `claude`** — every agent it births has the tools automatically.
Verb groups:

- **`channel_*`** — `inbox()` (my pending, rendered, with SLA), `send(to, topic, body)`,
  `reply(thread, body, confidence)`, `archive(id)`, `broadcast(body)`, `thread(id)`.
  All writes go *through* Styloagent, so the filename/header conventions are enforced
  centrally and the projection updates with zero watch-lag.
- **`context_*`** — `checkpoint(summary?)` (dehydrate → write my
  `saved-context/<prefix>-context.md`), `load()` (rehydrate). The environment owns the
  file + the "what a fresh me needs" structure.
- **`fleet_*`** — `status()` (who's live/ghost, on what worktree), `who_owns(scope)`
  (the routing/adjacency table as a query — used for correct routing and SLA redirects).
- **`worktree_*`** — `status()/diff()/log()` for my own state, no shelling.

**`styloagent` skill (the etiquette).** A Claude Code skill shipped by Styloagent and
auto-available to spawned agents, encoding the PROTOCOL *discipline* as guided
workflows on top of the verbs: answer-an-inbox (reply shape → `channel_reply` →
`channel_archive`), clean-handoff/dehydrate (`context_checkpoint` with the required
sections), heads-up-before-overlap (`fleet_who_owns` → drop a heads-up),
redirect-on-stale (the 10-minute ritual). **The MCP is *what you can do*; the skill is
*how to behave*.**

**Symmetry:** the human's cockpit actions (spawn / dehydrate / rehydrate / nudge) and
the agent's self-service verbs are the *same underlying operations from two sides* —
one API, two callers.

### 4.2 Delivery (broker → agent)

Because Styloagent owns the PTY and the turn boundary, a "nudge" is **a real
between-turns user turn**, not raw text jammed into stdin mid-response: the broker
waits for idle, then feeds e.g. *"📨 New message from `overview-` (thread:
composer-seam) — use the `styloagent` skill to review + reply."* Ack = observed
side-effect (the reply/archive lands via the MCP verbs, which the broker sees
instantly because it owns the writes). Raw stdin injection is kept only as a
**fallback** for sessions not connected to the MCP server. This retires the
"injecting mid-response is disruptive" hazard.

**Graceful degradation (reinforces §2):** if the MCP server is down, agents fall back
to the plain file-drop channel + their own `fswatch`. The verbs are a convenience
layer over the durable filesystem, never a replacement.

**MCP transport = local socket** (not stdio): one `styloagent` server serves the
whole fleet of agent sessions, rather than a 1:1 stdio pipe per agent.

### 4.3 Remote agents & the SSH control router (later slice)

A remote agent is just a **local Porta.Pty running an `ssh` session** — so the entire
terminal / lifecycle / MCP stack works unchanged; only *where the shell lives*
differs. Each manifest entry gains a **transport**: `local` or `ssh <host>`, with a
credential *reference* (keychain / ssh-agent / secret-store slug — **never a value**).

What's genuinely new is the **SSH control router** — an infrastructure-aware access
broker that is *aware of the whole setup*:

- **Live model of the setup:** machine inventory + reachability; what's running on
  each (perf test, build, deploy, load test); active SSH sessions; credential refs.
- **Hard rules (code, not LLM):** serialize auth per host so parallel agents never
  trip an account **lockout**; always-password / never the operator's account /
  one-attempt-then-stop (the PROTOCOL's SSH memories); pool + reuse connections.
- **Advise & gate (small/local LLM behind `IRouter`):** reason over that structured
  state to route work — *"x is running a perf test; your load test would interfere —
  try y."* Deterministic gating (lockout, creds) is code; the fuzzy routing/advice is
  the little model. Frontier specialists stay out of mechanical routing.
- **State source:** agents declare intent via an MCP `host_*` verb ("about to
  load-test y") + `fleet_status` correlation + light probing (load/uptime). The
  router turns "who's doing what" (already in the projection) into "what's safe to run
  where."

Security-sensitive (credentials + remote exec) and substantial, so the transport
abstraction and `IRouter` seam are seeded now, but implementation is a **later slice**
(may warrant its own spec).

---

## 5. UI

Everything is Dock-managed; the layouts below are the *default* arrangement — the
user re-arranges freely.

### 5.1 Terminals & the session lifecycle (the heart)

Each terminal is a first-class dockable **document**:

- **Rename** — display name, defaults to the prefix.
- **Set border color** — per-agent identity (propagates everywhere, §3.3).
- **Split** (side-by-side / stacked), **float** to its own OS window, drag between
  regions, group into tabs.
- Layout serialized to the presentation sidecar; the live PTY is never serialized.

Lifecycle, as UI actions:

| Action | Behavior |
|---|---|
| **Spawn** | Pick a roster entry → `cd <worktree>` → launch `claude` → paste launch-prompt. Tab appears in the agent's color. |
| **Dehydrate** | Deliver a "checkpoint your context" turn (§4.2); the agent calls `context_checkpoint` → **wait for `saved-context` to change (observed ack)** → kill PTY. Tab stays as a dimmed **ghost (○)** holding the restart-prompt ref. The dock slot outlives the process. |
| **Rehydrate** | On a ghost → spawn PTY → `cd` → paste `launch-prompts/<prefix>-restart.md` (which points at the agent's `saved-context`). Ghost → live. |
| **Nudge** | Deliver a between-turns user-turn (§4.2), not raw stdin; manual button *and* broker-automatic on delivery. |
| **Split** (striate) | New responsibility → new pane/tab; a fresh prefix + worktree is registered. |
| **Merge** (consolidate) | A prefix dissolves (e.g. `wba-atom-` folding into `foss-`) → close the pane; its saved-context folds into the parent. |

The dock arrangement ends up mirroring the org structure of the fleet: panes are
agents, splits are team boundaries, ghosts are agents spun down but not forgotten.

### 5.2 Bus viewer — "lucidVIEW"

The projection engine turns the flat `inbox/ outbox/ archive/` files into threads,
rendered as markdown by **`Mostlylucid.LucidView.Markdown`** — a control library
**extracted from lucidVIEW** (see §6.1) that carries lucidVIEW's presentation
decisions (layout, fonts, themes, mermaid, syntax highlighting) on top of
`LiveMarkdown.Avalonia`. LiveMarkdown's *incremental* rendering means streaming
content renders as it arrives.

- Thread list → thread view; each message shows from/to (color-accented),
  timestamp, state badge, rendered body (the reply shape — *Direct answer / Why /
  Relevant docs / Relevant code* — renders with its structure intact).
- **SLA/staleness front-and-center:** live countdown on directed messages awaiting
  a reply; on breach, the panel surfaces the exact PROTOCOL action — redirect to
  the closest agent (target pre-filled) and "revive via
  `launch-prompts/<prefix>-restart.md`".
- **Compose (write action):** new message / reply / broadcast — Styloagent writes
  the file with the correct prefix, slug, and header template, so the fragile
  filename convention is enforced by the tool, not by hand.
- **Archive:** one-click `mv` to `archive/` preserving the slug ("finish, then
  archive").
- **Web reference fetch (adjacent, via StyloExtract):** a URL referenced in a
  message (or the "Relevant docs" pointers) gets a "fetch & preview" affordance —
  `StyloExtract.Html` → clean markdown → rendered inline through the same lucidView
  renderer. Additive; lands after the core bus viewer, not in the MVP.

### 5.3 Worktree viewer — "what each agent is working on"

A `gwq`-style status strip, one row per agent, cross-repo:

```
● foss-     feature/atoms           a1b2c3d4  ✎3  ↑4 ↓0   "layer refactor step 2"   2m ago
● deploy-   main                    e5f6a7b8  ✓   ↑0 ↓1   "flush timing fix"        1h ago
○ dash-     (dehydrated)            9c0d1e2f  ✎7  ↑4 ↓0   "read-path WIP — held"   12h ago
```

Per row: branch • HEAD short-SHA • dirty count • ahead/behind • last-commit + relative
time. Live via narrow-scope `FileSystemWatcher` + debounce + safety poll (watcher
only triggers a git re-query). Click a row → focuses that agent's terminal; expand
→ `diff --stat`. A dehydrated agent shows its last-known worktree state.

### 5.4 System Map — Naiad / mermaid (offline)

Reuses **Mostlylucid.Naiad** (bundled with lucidVIEW) to render, offline:

- The **layer striation** — the specialist decomposition — as a diagram: the
  coordination document made visual.
- **Fleet topology:** prefixes as nodes, PROTOCOL scope-adjacency as edges
  (`deploy-↔foss-↔overview-`, `dash-↔edit-↔mae-`, `wba/wba-atom/caps-↔foss-`). Node
  color = agent identity; node state = live / ghost.
- **Active paths:** an edge lights up when a message is in flight; edges go SLA-hot
  (red) when a directed message is past its 10-minute window.

The diagram source is *generated mermaid text*, so the same artifact lives in the
coordination spec markdown and renders live in-app. Static map lands in Slice 2;
active-path highlighting alongside the broker in Slice 4.

---

## 6. Technology stack

Keep it lean, but **Native AOT is out of scope** — it fought the reflection-based
markdown renderer and isn't worth the pain. A plain trimmed self-contained publish
is fine.

| Layer | Choice | Notes / risk |
|---|---|---|
| Runtime | **.NET 10**, **Avalonia 11.3** | `LiveMarkdown.Avalonia` allows `[11.3.0,)` (open upper bound), so the renderer does not *cap* us; but the extracted presentation deps (Svg.Controls, CSharpMath, Naiad) are 11.3-built, so we target 11.3 now and revisit 12.x when they move. We own the terminal render regardless, so little is lost. |
| Docking | **Dock** (`wieslawsoltes/Dock`) 11.3.x | Only production-grade VS-style dock manager for Avalonia. Splittable/floating/tabbed; layout serialize/restore. |
| Markdown | **`Mostlylucid.LucidView.Markdown`** (extracted from lucidVIEW) | Control library carrying lucidVIEW's presentation decisions on `LiveMarkdown.Avalonia`. See §6.1. Incremental rendering. |
| Diagrams | **Mostlylucid.Naiad** | Offline mermaid → SVG; comes in via the extracted markdown lib and also drives the System Map. Zero *new* dependency. |
| Web fetch (adjacent) | **Mostlylucid.StyloExtract** (`.Html`/`.Markdown`/`.Heuristics`/`.Abstractions` 2.0.0) | HTML → clean markdown. Fetch a URL referenced in a message and render it through the same lucidView renderer. Additive, not MVP-gating (see §5.2). |
| PTY (load-bearing) | **Porta.Pty** (netstandard2.0) | `IPtyConnection.WriterStream.WriteAsync(...)` for command injection. Version-agnostic. Own this layer directly. |
| Terminal engine | **XTerm.NET** (headless VT engine) | Version-agnostic; render into a custom Avalonia-11 control we own (Iciclecreek 11.x if it proves out). |
| Git | **git CLI via CliWrap** (primary); **LibGit2Sharp** optional behind `IGitReader` | CLI sidesteps LibGit2Sharp's painful macOS-arm64 native-dylib bundling; matches/beats it on perf for our reads. |
| Change detection | `FileSystemWatcher` (narrow git-internal scope) + debounce + overflow-rescan + safety poll | macOS kqueue FD-exhaustion is the trap — keep watch scope narrow. |
| MCP host (agent API) | **.NET MCP SDK** (`ModelContextProtocol` C# SDK) | Styloagent hosts the `channel_*`/`context_*`/`fleet_*`/`worktree_*`/`host_*` tools (§4.1, §4.3) — a thin API over the engines — and injects the MCP config into each spawned `claude`. |
| MCP transport | **local socket** (one server, whole fleet) | Not stdio (1:1); the `styloagent` server serves every agent session (§4.2). |
| Config / manifest | **VYaml** (fast source-gen YAML) | Fleet manifest + presentation sidecar as human-editable YAML. |
| Remote transport | **`ssh` / `sshpass ssh` inside a local Porta.Pty**, gated by the SSH control router (§4.3) | A remote agent = a local PTY running ssh → reuses the whole terminal stack. Later slice. |
| SSH control router | infra-aware access broker + small/local LLM behind **`IRouter`** | Machine/state model + hard auth rules (code) + fuzzy routing/advice (LLM). §4.3. Later slice; security-sensitive. |

### 6.1 Extracting the lucidVIEW markdown presentation library

lucidVIEW's `MarkdownViewer` is today a `WinExe` **app** (Avalonia `11.3.12`) that
entangles two concerns: (a) its markdown **presentation brain** and (b) **app
chrome**. Styloagent wants only (a). So we extract a control-library NuGet —
**`Mostlylucid.LucidView.Markdown`** (working name) — and have lucidVIEW consume its
own extracted package (dogfooding; lucidVIEW stops being a monolith).

- **In scope (extract):** `LiveMarkdown.Avalonia` core (the scottgal fork carrying
  the un-upstreamed `<img>` width/height patch), **Naiad** mermaid wiring, syntax
  highlighting, the theme/font/layout resource dictionaries (Inter font, the six
  themes, code-block/table/spacing styling) — i.e. layout, fonts, mermaid, and
  rendering *decisions*.
- **Out of scope (stays in the app):** FluentAvaloniaUI window shell, QuestPDF
  print/PDF export, `StyloExtract` clipboard/import wiring, the User Manual.
- **Version:** the library carries whatever Avalonia floor its transitive
  presentation deps require (11.3-era today). This is what pins Styloagent to
  Avalonia 11.3 for now — not the renderer, which is version-flexible.
- **This is a prerequisite work item** that lands in the lucidVIEW repo and gates
  Slice 0-C. It should ship as a versioned NuGet so Styloagent takes a clean
  package dependency, not a submodule of the whole app.

---

## 7. Build order (slices)

Front-loads the riskiest primitive (owned terminal on Avalonia 11.3) before
committing to the shell.

- **Prerequisite (lucidVIEW repo) — extract `Mostlylucid.LucidView.Markdown`.**
  Carve lucidVIEW's presentation brain into a versioned NuGet (§6.1); lucidVIEW
  consumes its own package. Gates Slice 0-C and Slice 2.
- **Slice 0 — Spikes (throwaway, gate the stack).**
  - **A:** Porta.Pty spawns `claude` in a worktree on macOS; write to stdin
    programmatically *while the user types*; read stdout. Proves injection + focus
    coexistence.
  - **B:** Render that PTY in an Avalonia 11.3 control (Iciclecreek 11.x *or*
    XTerm.NET buffer → custom control). Proves the renderer on the pinned version.
  - **C:** the extracted `Mostlylucid.LucidView.Markdown` package, referenced from
    a fresh 11.3 app, renders a channel message (mermaid included).
  - **D:** spawn `claude` with a Styloagent-injected MCP config; confirm a trivial
    `mcp__styloagent__ping` tool round-trips (proves the agent-facing wiring +
    per-session config injection without polluting the repo).
- **Slice 1 — Walking skeleton: shell + ONE owned terminal.** Dock shell; manifest
  seeded from the channel; spawn one agent (cd → `claude` → launch-prompt);
  rename + border color; layout persists; dehydrate (save-context ack → kill) /
  rehydrate (restart-prompt → respawn). Proves the whole core loop for one agent.
- **Slice 2 — Fleet + bus (lucidVIEW) + agent surface + static System Map.**
  Multiple agents + roster + color propagation; channel projection engine (threads,
  states, SLA); bus panel renders threads; **broker becomes the MCP host** with the
  `channel_*`/`context_*`/`fleet_*`/`worktree_*` verbs (§4.1); **idle-gated user-turn
  delivery** (§4.2) + observed ack; the **`styloagent` etiquette skill**;
  compose/reply/archive; static topology map via Naiad.
- **Slice 3 — Worktree viewer.** `IGitReader` via CliWrap; per-agent status strip;
  live watch + poll; diff detail; click-to-focus.
- **Slice 4 — Broker polish.** SLA-breach auto-redirect, revive suggestions,
  staleness visualization, active-path highlighting on the System Map.
- **Slice 5 — Remote agents & SSH control router (later; security-sensitive).**
  `ssh`-in-PTY transport; central connection broker (auth serialization → no lockout,
  credential *references*, one-attempt-then-stop, never the operator's account); infra-aware
  routing/advice via `IRouter` (small/local LLM) + the `host_*` MCP verbs (§4.3). May
  warrant its own spec + a security review.

Out of scope (separate future sub-project): **P4 orchestration bootstrap / initial
ingest** — point it at a codebase folder → read the **git history** (commit
co-change, directory structure, ownership patterns reveal the natural module
boundaries) → spin agents to decompose the app structure into layers → emit the
initial coordination spec + focused specialist contexts + launch/restart prompts.
The striation mirrors the distributed structure the engineering teams already have.
This is a separate tool/phase; the cockpit *consumes* what it produces.

**Home repo:** `~/RiderProjects/styloagent/` (an app, not a library atom).

---

## 8. Testing

- **Channel projection engine** — fixture channel directory → assert thread /
  message-state model + SLA computation.
- **Git reader** — fixture repos/worktrees → assert porcelain parse.
- **Session/PTY host** — integration test spawning a trivial process (`bash`/`cat`)
  → assert write→read round-trip + idle detection + dehydrate-ack timeout.
- **Manifest seeding** — fixture channel → assert seeded manifest.
- **View-models** — `Avalonia.Headless`. Docking ergonomics verified manually.

---

## 9. Error handling (filesystem-is-truth → degrade, never destroy)

- Spawn fails → agent stays a ghost, no crash. Channel dir missing → empty panel.
- **Delivery is idle-gated (§4.2);** if the agent is mid-turn, queue the nudge +
  show "pending" — never blast text mid-response.
- **MCP server down → agents fall back** to the plain file-drop channel + their own
  `fswatch`; raw stdin nudge is the fallback delivery path. The verbs are convenience
  over durable files, never a hard dependency (§2).
- **Dehydrate ack timeout:** if `saved-context` does not change within the window,
  **do not kill** — warn. Never lose an agent's context.
- **SSH is lockout-guarded (§4.3):** serialize auth per host (never parallel), always
  password, never the operator's account, one-attempt-then-stop, credential *references*
  only — the PROTOCOL's SSH rules enforced centrally by the control router.
- git-not-on-PATH under a Finder-launched `.app` → resolve git's absolute path;
  degrade to "git unavailable."
- `FileSystemWatcher` overflow → full rescan; low-frequency safety poll as backstop.

---

## 10. Gating risks

1. **Terminal on Avalonia 11.3** — lucidVIEW pins 11.3; the freshest terminal
   control targets 12.x. Mitigation: own the XTerm.NET-rendered control (Spike B).
2. **stdin injection vs keyboard focus** — Spike A must prove they coexist.
3. **N live PTYs render scale** on macOS — measure in Slice 2; virtualize offscreen
   tabs; apply backpressure on chatty readers.
4. **Single-maintainer terminal ecosystem** (Porta.Pty / XTerm.NET) — mitigated by
   owning the PTY layer; renderer is swappable.
5. **PROTOCOL.md routing table is prose** — seed the manifest once + human-confirm;
   do not continuously re-parse prose.
6. **MCP config injection into spawned Claude Code sessions** — confirm the exact
   mechanism (`--mcp-config` / a session-scoped `.mcp.json` / settings) injects the
   `styloagent` server per-session *without* polluting the worktree repo (Spike 0-D).
7. **SSH credentials + remote exec (security-sensitive, Slice 5)** — hold credential
   *references* only (keychain / ssh-agent), never values; serialize auth to avoid
   account **lockout**; honor never-the-operator's-account / one-attempt-then-stop. Warrants a security
   review and likely its own spec.

---

## 11. Open questions

- **lucidVIEW reuse seam:** *Resolved* — extract `Mostlylucid.LucidView.Markdown`
  as a versioned NuGet (§6.1). Residual: final package name + the exact
  presentation-vs-chrome cut line, settled during the extraction work item.
- **Manifest index location:** *Leaning resolved* — it's just another focused
  document, so it lives in the channel dir (shared + git-committed, so agents can
  read it via `fleet_*`); presentation state (colors/layout/geometry) stays in
  Styloagent's own config. *Format resolved:* YAML via VYaml.
- **Channel path configurability:** `/tmp/agent-channel` is the current instance;
  the client should treat the channel root as a configurable workspace.
- **MCP transport:** *Resolved* — local socket (one server, whole fleet). Residual:
  the exact per-session config-injection mechanism (Spike 0-D, risk #6).
- **SSH credential store (Slice 5):** OS keychain vs ssh-agent vs a secret-store slug
  — never plaintext, never in the manifest (§4.3).
- **Router model (Slice 5):** which small/local LLM sits behind `IRouter`, and how
  much routing is LLM vs the deterministic adjacency table (§4.3).
- **StyloExtract scope:** keep web-fetch strictly additive (a preview affordance),
  not a crawler — bound what a "fetch & preview" pulls, and never auto-fetch on
  message render.
