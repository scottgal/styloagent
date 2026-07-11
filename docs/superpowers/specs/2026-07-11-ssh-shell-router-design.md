# Deterministic SSH/Shell Router — Design

**Date:** 2026-07-11
**Status:** Approved (design), pending implementation plan
**Author:** (with human)

## Goal

A **deterministic control plane** that lets Styloagent's agents safely share access to real
environments (SSH hosts, deploy targets, shared test boxes) without stepping on each other or
tripping account lockouts. Agents **claim** exclusive (or slot-limited) use of a resource, work
under a **lease**, and **release** it — all coordinated through a markdown file-drop ledger, the
same medium as the message bus. **No LLM runs inside the router**: contention is resolved by pure,
deterministic rules.

Two profiles of one primitive:
1. **Environment control (SSH):** a resource is an *account-on-an-environment* (e.g. `deploy@prod`);
   held exclusively; lockout-safe.
2. **Test slots:** a resource is a *pool of N interchangeable slots* on an environment; up to N
   agents (deploy/build agents) hold slots concurrently to coordinate builds.

## Why (motivation)

As the fleet grows, agents touch shared infrastructure: SSHing to hosts, deploying, running builds
on shared test boxes. Two failure modes bite:
- **Collisions:** two agents (or an agent and a human) using the same box/account at once.
- **Lockouts:** concurrent or repeated failed SSH auths trip `fail2ban`/PAM tally and lock the
  account — for *everyone*, including humans. A lockout during a deploy is a serious outage.

The message bus already proved that a markdown file-drop ledger is a good coordination medium for
the fleet. This applies the same idea to *resource arbitration*.

## The four load-bearing decisions

1. **Advisory coordinator, not an enforcing gateway.** The router is a markdown ledger + a pure
   resolver; it is never in the SSH connection path. Agents cooperate: claim → wait for grant →
   connect → heartbeat → release. (Chosen over a live proxy daemon for simplicity, determinism, and
   fit with the existing bus. A future enforcing shim is possible — see Out of Scope.)
2. **Exclusivity + attempt budget**, on an *account-on-environment* resource. Exclusivity (one
   holder) stops the concurrent-login pile-up; an attempt budget with cooldown stops the
   failure-rate cause. Both together.
3. **Lease + heartbeat** for reclamation. A grant is a lease with a TTL; the holder renews it while
   working; an unrenewed lease expires and is auto-released. A dead agent cannot hold forever.
4. **Capacity unifies the two profiles.** A resource has a `capacity`: `1` = exclusive account
   (SSH profile); `N` = a test-slot pool. The resolver grants up to `capacity` holders. Same
   claim/lease/heartbeat/queue for both.

## Architecture

Mirrors the message-bus split (pure projection → pure resolver → I/O coordinator), so it drops into
existing patterns (`ChannelProjection`, `FleetGovernor`, `MessageDelivery`).

```
Styloagent.Core.Router  (UI-free, pure)
├── RouterLedger.cs         # record types: Resource, Claim, Grant, AttemptLog, RouterState
├── RouterProjection.cs     # reads the ledger dir tree -> RouterState (pure parse, tolerant)
├── RouterResolver.cs       # PURE: (RouterState, now) -> IReadOnlyList<RouterDecision>
├── RouterPolicy.cs         # Resource config (capacity, lockout budget/window/cooldown) + reader
└── RouterDecision.cs       # Grant / Queue / Expire / EnterCooldown / ClearCooldown / Release

Styloagent.App
├── Router/RouterCoordinator.cs   # ticks + file-watch; applies decisions (writes grants, logs,
│                                 # notifications); marshals to the fleet
├── Mcp/RouterTools.cs            # MCP tools: claim / heartbeat / release / log_attempt / status
└── Views/RouterView.axaml        # a Router panel tab: live holders / queues / cooldowns / slots
```

- **`RouterProjection`** — like `ChannelProjection`: enumerates `router/<env>/…/{claims,grants}/*.md`
  + `attempts.md` + `resource.yaml`, parses headers, returns an immutable `RouterState`. Never
  throws; a malformed file is skipped.
- **`RouterResolver`** — the heart, a **pure function** `Resolve(RouterState state, DateTimeOffset now)`
  → decisions. No I/O, no LLM, no ambient clock (now is passed in, like the existing pure logic).
- **`RouterCoordinator`** — the only component that does I/O: runs the resolver on a debounced tick
  and on ledger file-change, writes/removes grant files, appends to `log.md`, and emits
  notifications (bus message + panel refresh). Marshals to the UI thread like `FleetController`.

## Ledger file structure

```
.styloagent/router/
  <env>/                              # environment id, e.g. prod, staging, testbox-1
    accounts/
      <account>/                      # SSH profile: resource = account-on-env, e.g. deploy
        resource.yaml                 # capacity: 1 (default); lockout: {budget, window, cooldown}
        claims/<ts>-<prefix>.md       # a claim request
        grants/<prefix>.md            # an active grant/lease (heartbeat = touch mtime)
        attempts.md                   # rolling auth attempt log
        log.md                        # audit trail
    slots/
      <pool>/                         # test-slot profile: resource = pool of N slots, e.g. ci
        resource.yaml                 # capacity: N; no lockout block
        claims/<ts>-<prefix>.md
        grants/<prefix>.md            # up to N concurrent grant files
        log.md
```

**Claim file** `claims/<iso8601>-<prefix>.md` (drop-once; timestamp in the name gives FIFO + slug
uniqueness, like the channel):
```
**From:** foss-
**Timestamp:** 2026-07-11T12:00:03Z
**Purpose:** deploy release 2.9.0
```

**Grant file** `grants/<prefix>.md` (written by the coordinator; the holder heartbeats by touching
its mtime; release = the holder deletes it, or the coordinator deletes on expiry):
```
**Holder:** foss-
**Granted:** 2026-07-11T12:00:04Z
**Expires:** 2026-07-11T12:02:04Z
**ClaimTimestamp:** 2026-07-11T12:00:03Z
```
Heartbeat is the file's mtime (agent `touch`es it, or the `heartbeat` MCP tool does). `RouterProjection`
captures each grant's mtime into `RouterState` (as `Grant.HeartbeatAt`), so the resolver stays **pure**:
it treats a grant as live when `now - HeartbeatAt < leaseTtl` and re-stamps the `Expires` field on
renewal. `Expires` is a human-readable convenience; **mtime (`HeartbeatAt`) is the authoritative
liveness signal** — this keeps heartbeat a zero-parse `touch`.

**attempts.md** — append-only lines the holder writes after each SSH auth, newest last:
```
2026-07-11T12:00:05Z ok
2026-07-11T12:03:11Z fail
2026-07-11T12:03:14Z fail
```
The resolver derives the rolling-window failure count and cooldown state from these lines +
`resource.yaml`'s lockout config; it appends a `# cooldown-until <iso>` marker line when it trips.

**resource.yaml** (tolerant, VYaml, mirrors `fleet.yaml`/`priority-policy.yaml`):
```yaml
capacity: 1              # 1 = exclusive account; N = slot pool
lockout:                 # omit entirely for slot pools
  budget: 5              # failures allowed within the window before cooldown
  window: 10m            # rolling window
  cooldown: 15m          # how long the account is refused after tripping
leaseTtl: 2m             # heartbeat interval budget (default; can be per-resource)
```

## Claim / grant / lease state machine

1. **Claim.** Agent wants account A on env E → drops `accounts/A/claims/<ts>-<prefix>.md`.
2. **Grant/queue.** Resolver, per resource: count live grants; if `liveGrants < capacity` **and** the
   account is **not cooling**, grant the earliest-timestamped un-granted claim (coordinator writes
   `grants/<prefix>.md` with `Expires = now + leaseTtl`, logs, notifies). Otherwise the claim waits;
   its **queue position** = its rank among un-granted claims by timestamp.
3. **Work + heartbeat.** The holder connects and, while working, `touch`es its grant file (or calls
   the `heartbeat` MCP tool) at < `leaseTtl` intervals. The coordinator re-stamps `Expires`.
4. **Release.** On done, the holder deletes its grant file (or calls `release`). Resolver promotes
   the next queued claim.
5. **Expiry.** If `now - grant.mtime ≥ leaseTtl` (no heartbeat), the resolver marks the grant
   **expired**; the coordinator deletes it, logs `expired`, notifies, and promotes the queue head.

FIFO by claim timestamp; ties broken by filename (prefix) for determinism.

## Lockout safety

- After each SSH auth the holder appends `<iso> ok|fail` to `attempts.md` (via the `log_attempt`
  MCP tool, or a thin wrapper the agent runs).
- The resolver computes `failuresInWindow` = count of `fail` lines with `ts ≥ now - window`. When
  `failuresInWindow ≥ budget`, the account enters **cooldown** until `now + cooldown`; while cooling,
  **no new grants are issued** for that account and a `cooldown-entered` notification fires. A single
  `ok` line clears the failure streak (resets the counter to 0 from that point).
- Exclusivity (capacity 1) means only the holder is ever auth'ing, so the failure counter reflects
  one actor — no cross-agent contention on the counter.
- Cooldown is **per account**, independent of any current holder: it protects the *account* (and
  thus humans) even after the failing holder releases.

## The two profiles via capacity

| | SSH environment control | Test slots |
|---|---|---|
| Resource | `accounts/<account>/` | `slots/<pool>/` |
| `capacity` | `1` (exclusive) | `N` |
| Lockout tracking | on (`attempts.md`, cooldown) | off (no auth) |
| Typical holder | any agent needing that login | deploy/build agents |
| Use | "hold `deploy@prod` while I ship" | "grab 1 of 3 CI slots, run the build, release" |

Identical claim/grant/lease/heartbeat/queue machinery; the only differences are `capacity` and
whether a `lockout` block is present.

## Notifications & agent interface

**No LLM in the router.** The coordinator emits notifications on: `granted`, `queued` (contention),
`released`, `expired` (lease reclaimed), `cooldown-entered` (near/at lockout), `cooldown-cleared`.

- **Message bus:** a bus message is dropped to the affected agent (and/or overview) — reuses the
  existing channel + priority delivery (a `cooldown-entered` is `urgent`; a `queued` is `info`).
- **Router panel:** a new tab showing live state per environment — holders, queue depth/positions,
  cooldown timers, slot occupancy — read straight from `RouterProjection` (like the Bus/Issues tabs).

**MCP tools** (in `RouterTools`, alongside `FleetTools`; deterministic, no LLM):
- `claim(env, resource, purpose)` → `granted | queued(position)` (drops the claim; returns current
  disposition).
- `heartbeat(env, resource)` → renews the lease (touch/ re-stamp).
- `release(env, resource)` → drops the grant.
- `log_attempt(env, account, ok)` → appends to `attempts.md`.
- `router_status(env?)` → the current holders/queues/cooldowns (for an agent to poll).

An agent's flow: `claim` → poll/await `granted` → `ssh` → `log_attempt` per auth → `heartbeat`
periodically → `release`.

## Error handling & edge cases

- **Missing/empty ledger dirs** → treated as "no claims / free"; projection never throws (mirrors
  `ChannelProjection`).
- **Malformed claim/grant file** → skipped by the projection; logged.
- **Clock:** `now` is injected into the resolver (pure); the coordinator supplies wall-clock. All
  timestamps ISO-8601 UTC. Heartbeat uses file mtime to avoid clock-parse coupling.
- **Dead holder:** covered by lease expiry (decision #3).
- **Duplicate claim** by the same prefix for the same resource → the latest supersedes; a holder
  re-claiming an already-held resource is a no-op (idempotent).
- **Race on grant write:** only the single coordinator writes grants (one writer), so there is no
  multi-writer race; agents only write their own claim/heartbeat/attempt files.
- **Concurrent capacity:** for `capacity: N`, the resolver grants exactly up to N; the (N+1)th claim
  queues.

## Testing

- **`RouterResolver` (pure) — exhaustive unit tests** (the bulk; like `FleetGovernor`/`MessageDelivery`):
  FIFO grant, queue promotion on release, lease expiry + promotion, `capacity: N` (grants exactly N,
  queues the rest), cooldown after `budget` failures in window, cooldown refusal, cooldown clear on
  success/elapse, tie-break determinism, empty/edge states. All with an injected `now`.
- **`RouterProjection` — parse tests** with fixture ledger trees (claims/grants/attempts/resource.yaml).
- **`RouterPolicy` reader — tolerant-config tests** (missing file → defaults; capacity/lockout parse).
- **Coordinator — integration** against a temp ledger dir: claim → grant file appears; no heartbeat →
  grant expires + next promoted; release → promotion. (Opt-in/time-controlled.)
- **MCP `RouterTools`** — routing + auth, faked coordinator (like `FleetToolsTests`).
- **Router panel** — headless render test (like `IssuesViewTests`).

## Out of scope (now) / future

- **Enforcing shim:** a thin `env-ssh` wrapper agents call instead of raw `ssh`, which checks the
  ledger (hold the grant? account within budget?) and refuses/records otherwise — turns the advisory
  system bypass-resistant without a central daemon. A natural follow-on; not this spec.
- **Credential custody / a live SSH proxy daemon.** Explicitly rejected for this cut.
- **Cross-project / global ledger** (a shared router across projects) — this cut is per-project under
  `.styloagent/router/`.
- **Priority claims / preemption** (a high-priority claim jumping the queue or preempting a holder) —
  FIFO only for now.
