# Issue Triage — 2026-07-13 ("from-the-inside" session)

Surfaced by operating the fleet live. ~15 issues; grouped by theme, with owner (per the architecture
ownership map), severity, and status. Then a recommended sequence.

## ✅ Fixed / landed this session
- **Terminal pane won't scroll** + **prompts unreachable** (`docked-...-no-scrollbar`, `...prompts-unreachable`) — cockpit- **Fix 1**, merged to `main` (`efeac31`). *Needs app rebuild+restart to take effect.*
- **worktree-survives-spawn** feature — cockpit-, 505 tests green on `fix/worktree-survives-spawn` (ready to merge).
- **Clean-build Naiad ref** — cockpit-, on `main` (`c184981`).

## Themed backlog

### A. Coordination & delivery — highest leverage
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `root-message-delivery-is-terminal-injection-only` (MCP-native delivery) | high | bus- | open (held) |
| `bus-delivered-messages-inject-but-dont-submit` | high | session- | in Fix 3 |
| `urgent-deliverys-esc-break-doesnt-interrupt` | high | session- | in Fix 3 |
*Fixing the MCP-native root dissolves the two symptoms for connected agents; session- makes the injection **fallback** correct in parallel.*

### B. Governance — the meta-fix
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `enforce-ownership-boundaries` (cross-owner edit needs a prod) | high | overview- + hook layer | open |
*Every collision today traces to its absence. A `PreToolUse` gate on the ownership map. Encoded as a PROTOCOL rule already; this is the enforced version.*

### C. Worktree / build infra — unblocks isolation
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `worktree-builds-cant-resolve-lucidview` (relative cross-repo ref) | high | spawn infra + App csproj | bridged (symlink); needs durable fix |
| `cant-hand-a-worktree-isolated-agent-a-mission-doc` | med | spawn workflow | open |

### D. Lifecycle
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `killed-agent-keeps-stale-needs-you` | med | session- | in Fix 2 |

### E. Cockpit UX (document surface + roster)
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `document-search-...-shows-the-whole-doc-in-a-hover` | high | cockpit- | open |
| `dragging-anything-onto-the-document-surface` (open the viewer) | med | cockpit- | open |
| `closing-the-last-document-in-a-dock-area` (close empty area) | med | cockpit- | open |
| `add-a-hide-action` (keep working, off-screen) | med | cockpit- | open |

### F. Observability
| Issue | Sev | Owner | Status |
|---|---|---|---|
| `agent-performed-git-operations-are-near-invisible` | low | cockpit-/repo- | open |

## Recommended sequence
1. **Governance — enforce ownership boundaries (B).** De-risks every future parallel step; stops collisions structurally. *Do first.*
2. **Delivery — MCP-native root (A) + session- fallback.** Kills the manual-relay tax that's slowed this whole session. Highest day-to-day leverage.
3. **Worktree build infra (C).** Durable symlink-on-spawn (or lucidview→NuGet) so isolated agents "just build."
4. **Lifecycle (D).** session- Fix 2 (already in flight).
5. **Cockpit UX (E).** search-hover (high) → drag-to-open → close-empty-area → hide.
6. **Observability (F).** git-op visibility. Low.

Merges pending on `main`: `fix/worktree-survives-spawn` (feature), session- Fix 2/3, docs- manual.
