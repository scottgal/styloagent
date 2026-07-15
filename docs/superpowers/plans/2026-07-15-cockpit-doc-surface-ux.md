# Mission: Document-surface + Dock UX (cockpit-)

**Owner:** `cockpit-` (App shell / Dock / document surface). Work these in order; branch off `main`,
commit per fix, tests green, report each over the bus. Stay in your files (PROTOCOL rule); session- is
on PtyMessageInjector/AgentPaneViewModel concurrently.

## Fix C — Search opens a doc window, not a hover (HIGH)
Selecting a document-search result renders the WHOLE doc in a hover/flyout (unusable). Make it **open
the doc as a new dock document** (rendered markdown / source viewer), reusing the Document Library
open path. Issue: `document-search-selecting-a-result-shows-the-whole-doc-in-a-hover`.
Files: the top-bar search box/result wiring (MainWindowViewModel + search view); the DocLibrary
open-document path.

## Fix D — Drag anything to the doc surface → open its viewer (MED)
Dropping a draggable entity (library file, git-changed file, bus thread, timeline file-op, diagram)
on the document surface should open the appropriate viewer as a dock document. Build a **viewer-by-type
dispatch** and reuse it from Fix C. Issue: `dragging-anything-onto-the-document-surface`.

## Fix E — Close last document in an area → close the area (MED)
When the last document in a dock region is closed, the empty area lingers. Make it collapse/close so
the layout reflows. Issue: `closing-the-last-document-in-a-dock-area`. Files: StyloagentDockFactory /
RebuildCenterLayout.

## Fix F — Hide an agent pane while it keeps running (MED)
A "hide" action: take a live agent's pane off-screen (PTY stays running, still shows "working" in the
roster) with a way to restore it — distinct from Dehydrate (which kills the PTY). Issue:
`add-a-hide-action`. Files: StyloagentDockFactory / MainWindowViewModel / AgentPaneViewModel + a roster
affordance.

(Git-op visibility on panel/timeline is queued separately — lower priority, spans repo-/timeline.)
