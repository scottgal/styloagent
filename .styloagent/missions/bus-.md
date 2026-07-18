# bus- (fresh) — Bug A ONLY: per-repo messaging Core to open a 2nd federated instance

You are a FRESH bus- — the prior instance stranded itself on `wrap_up` (worktree removed, agent not
exited) and was reaped by the operator. Issue `wrap-up-strands-a-still-live-agent` is filed.

You have NO worktree by design. Work on the SHARED main tree, commit straight to main staging ONLY your
own Core/Channel files (targeted `git add <paths>`, never -A/-am). **Do NOT call wrap_up** — that's what
stranded the last instance. Commit-to-main is your handoff.

Scope: ONE thing — **Bug A: repo-qualified messaging Core (Model A)** so a second repo
(stylobot-commercial) opens as an INDEPENDENT federated instance (its own .styloagent/channel/, fleet,
router). Stay OUT of BusViewModel / MainWindowViewModel (cockpit-'s) — you provide Core seams + a diff only.

LOCKED design decisions from overview-:
- Repo identity KEY = canonical repoRoot (from repo-'s `IGitService.ResolveRepoRootAsync`; subpaths
  collapse to the same root). A workspace display name may ride alongside for UI, but the routing/dedupe
  key is repoRoot.
- Surfacing = each instance gets its OWN bus pane over its OWN channel (NOT a merged repo-dimension feed).
- Model A: per-repo ChannelDeliveryCoordinator; prefix-only routing WITHIN a channel; (repo,prefix) =
  (which-channel, prefix); RecipientsFor stays prefix-only. `all-@*` = N physical copies (one per repo
  channel); degrade-never-destroy per repo. `repo` param on `send_message` defaults to sender's repo
  (back-compat free).

BUILD (TDD in isolation with fakes) — cockpit- landed P1 slice-1 (1183b2c) and needs EXACTLY these to
swap its `StubRepoInstanceOpener` 1:1 (its port: `interface IRepoInstanceOpener { Task OpenAsync(string
repoRoot, CancellationToken ct); }`):
  1. **repo→channel RESOLVER**: given a canonical repoRoot, return its channelRoot
     (repoRoot/.styloagent/channel) + the channel's agent prefixes — a blessed Core helper
     (WorkspaceConfig.SingleRepo(repoRoot)-style projection) so cockpit-'s keying matches your routing.
  2. **per-repo DELIVERY COORDINATOR**: a ChannelDeliveryCoordinator/MessageDeliveryService bound to THAT
     repo's channel + its own PendingInbox/hooksDir, so the 2nd instance delivers independently.
  3. **From-Repo header + `BusMessage.FromRepo` + `ChannelProjection` parse**; confirm routing keyed by
     (repoRoot, prefix). If you edit ChannelProjection.cs, give cockpit- a heads-up first (it reads it).
HOLD live N-coordinator wiring in MainWindowViewModel — provide the Core seam + a diff for cockpit-.

Report the resolver signature + coordinator seam + repo-key semantic on the bus to overview- for relay
to cockpit-. P0 (bus-viewer Seen) is DONE and App-owned — do NOT build any Core Seen-state.
