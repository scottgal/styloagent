# Mission: Bus + Issues panel UX (cockpit-)

**Owner:** `cockpit-` (App shell / UI). These are the top day-to-day annoyances after scroll (which is
already fixed on main). All in your domain. Branch off `main`; commit per fix; TDD where there's a seam
(BusViewModel/IssuesViewModel have headless tests).

## Fix A — Signal Bus: messages clear out (HIGH)

Replied/archived threads don't leave the active bus → it fills with stale threads and stops being
glanceable. Make thread state drive the view: a **replied** or **archived** thread leaves *Needs
attention* / *Recent* and moves to *Archive* (or clears). Verify: reply to / archive a thread → it
leaves the active groups immediately.
Files: `BusViewModel` (+ its thread-grouping), `ChannelProjection` thread-state transitions, `BusView`.
Issue: `signal-bus-replied-archived-messages-dont-clear`.

## Fix B — Issues panel: expand + clear (HIGH)

1. **Expand to read** — an issue currently shows only its title; make it **expand to show the full
   detail + severity**, mirroring how the bus expands a thread. (Right now every filed issue's body is
   unreadable in the UI.)
2. **Resolve/clear** — add resolve/dismiss so a handled issue leaves the active list (optionally an
   archived filter). The list only grows today.
Files: `IssuesViewModel` / `IssuesView` / `IssueStore` (may need a `Resolved`/`state` field + a
resolve action). Issue: `issues-panel-cant-expand-or-clear`.

## Boundaries & coordination

Stay in your files (App UI). Anything outside your domain → STOP and ping overview- (PROTOCOL rule).
session- is concurrently on Fix 3 (PtyMessageInjector) / Fix 2 (AgentPaneViewModel) — different files.
Commit per fix; `dotnet build` + tests green; report each fix over the bus.
