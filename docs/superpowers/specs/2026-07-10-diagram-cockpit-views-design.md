# Diagram-driven Cockpit Views — System Map + Bus Sequence

**Status:** Design — pending approval
**Date:** 2026-07-10
**Author:** Styloagent

---

## 1. Goal

Turn live cockpit state into rendered diagrams. Two pure Core generators emit **mermaid markdown**
that opens as a rendered document through the existing `OpenMarkdownDocument` → `LucidMarkdownView` /
Naiad path:

- **System Map** — the agent fleet tree (Theme 4's `Prefix` / `ParentPrefix` / `Responsibility` /
  hook-state) as a mermaid `graph TD`: overview → subsystems → children, nodes styled by state.
- **Bus Sequence** — the channel threads (`BusMessage.From` + `Timestamp`, grouped by `Slug`) as a
  mermaid **flowchart** (`graph LR`): each thread's messages become a left-to-right chain of
  sender→sender edges labelled by slug, ordered by time. (Naiad's Avalonia surface renders only
  flowcharts — see §6 — so this is a flowchart, not a `sequenceDiagram`.)

Diagrams are **on-demand** by default (a button generates from current state, a Refresh regenerates);
a per-diagram **Live** toggle opts into debounced auto-refresh on fleet/bus changes.

Builds on: the fleet lineage (`AgentPaneViewModel.{Prefix, ParentPrefix, Responsibility, HookState}`),
the channel model (`BusMessage`, `ChannelProjection`), and the doc-render path
(`MainWindowViewModel.OpenMarkdownDocument`, `MarkdownDocumentViewModel`, `MarkdownDocumentView` →
`LucidMarkdownView`).

---

## 2. Scope

**In scope**
- `SystemMapGenerator` + `BusSequenceGenerator` (pure Core, mermaid markdown).
- `MarkdownDocumentViewModel.FromMarkdown(title, markdown)` — open generated content with no temp file.
- `DiagramDocumentViewModel : MarkdownDocumentViewModel` — a `Live` toggle + a `Refresh` regenerate.
- `MainWindowViewModel` commands `ShowSystemMap` / `ShowBusSequence`; a debounced watcher that
  regenerates every open **Live** diagram on `Panes`/bus changes.
- **System Map** + **Bus Sequence** buttons in the Documents panel; a ⟳ Refresh + ☐ Live row on a
  diagram document.

**Out of scope (later)**
- Strict C4 notation; click-node-to-focus-agent; diagram export/save; zoom/pan beyond LucidView's;
  diagrams of saved-context docs.

---

## 3. Generators (pure Core)

**`Diagrams/SystemMapGenerator.cs`**
```csharp
public sealed record FleetNode(string Prefix, string? ParentPrefix, string Responsibility, string State);
public static class SystemMapGenerator { public static string Build(IEnumerable<FleetNode> nodes); }
```
Emits `# System Map\n\n` then a fenced ` ```mermaid ` block: `graph TD`, one node per agent
(`id["prefix<br/>responsibility"]`, id = a sanitized prefix), a parent→child edge per node with a
parent, and a `classDef`/`class` per hook-state (working/idle/needsYou/exited) for colour. Nodes are
emitted in a deterministic order (by prefix). **Empty input → a valid `graph TD` with a single
`note["no agents yet"]` node** (never an empty/invalid diagram).

**`Diagrams/BusSequenceGenerator.cs`**
```csharp
public sealed record SeqMessage(string From, DateTimeOffset? When);
public sealed record SeqThread(string Slug, IReadOnlyList<SeqMessage> Messages);
public static class BusSequenceGenerator { public static string Build(IEnumerable<SeqThread> threads); }
```
Emits `# Bus Sequence\n\n` then a fenced ` ```mermaid ` **flowchart** block: `graph LR`, one node per
distinct sender (sanitized id, prefix label), and for each thread — messages ordered by `When` — a
directed edge between each pair of **consecutive distinct senders** labelled by slug
(`a -->|slug| b`). A thread with a single distinct sender emits a self-note node
(`a -->|slug awaiting| a` is invalid in mermaid, so instead add an `awaiting["prefix: slug (awaiting reply)"]`
node and edge `a --> awaiting`). **Empty input → a valid `graph LR` with a single
`note["no bus activity yet"]` node.** (Flowchart, not `sequenceDiagram`, because only flowcharts render
on Naiad's Avalonia surface — §6.)

Both generators are deterministic and never throw. Prefix/participant ids are sanitized to a mermaid-
safe token (`[A-Za-z0-9_]`), with the original prefix shown in the label.

---

## 4. App components

**`ViewModels/MarkdownDocumentViewModel.cs` (modify):** add
```csharp
public static MarkdownDocumentViewModel FromMarkdown(string title, string markdown);
```
which sets the `Markdown` content directly and leaves `SourcePath` empty (no file read). The existing
file-path ctor is unchanged.

**`ViewModels/DiagramDocumentViewModel.cs` (create):** `: MarkdownDocumentViewModel`.
```csharp
public enum DiagramKind { SystemMap, BusSequence }
// ctor: (string title, DiagramKind kind, Func<string> generate) → sets Markdown = generate()
public DiagramKind Kind { get; }
[ObservableProperty] private bool _live;
public void Refresh();          // Markdown = _generate()
```
`Refresh()` regenerates now. `Live` is a plain observable flag the shell watches.

**`ViewModels/MainWindowViewModel.cs` (modify):**
- `[RelayCommand] void ShowSystemMap()` — build `FleetNode[]` from `Panes`
  (`new FleetNode(p.Prefix, p.ParentPrefix, p.Responsibility, p.HookStateText)`), open
  `new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap, () => SystemMapGenerator.Build(BuildFleetNodes()))`
  via `OpenMarkdownDocument`, and track it in `_openDiagrams`.
- `[RelayCommand] void ShowBusSequence()` — build `SeqThread[]` from the channel projection / bus
  (group messages by `Slug`, project `From`+`Timestamp`), open a `DiagramDocumentViewModel` similarly.
- A single `DispatcherTimer`/debounce (~500 ms) armed on `Panes.CollectionChanged` and the bus refresh
  signal; on fire, for each tracked diagram with `Live == true`, call `doc.Refresh()`. Toggling `Live`
  off simply means the timer skips it. Diagrams are untracked when their document is closed.

**`Views/DocLibraryView.axaml` (modify):** a **System Map** and a **Bus Sequence** button in the panel
header, bound to the two commands.

**`Views/MarkdownDocumentView.axaml` (modify):** a small top row — ⟳ **Refresh** (bound to a `Refresh`
command) and a ☐ **Live** `ToggleButton` — visible only when the DataContext is a
`DiagramDocumentViewModel` (via a converter or an `IsVisible` bound to a `IsDiagram` flag).

---

## 5. Data flow

```
click "System Map" → ShowSystemMap → FleetNode[] from Panes → SystemMapGenerator.Build → md
  → new DiagramDocumentViewModel(SystemMap, generate) [Markdown = md] → OpenMarkdownDocument
  → MarkdownDocumentView → LucidMarkdownView / Naiad renders the graph
toggle Live on → shell debounce watches Panes + bus → on change → doc.Refresh() (Markdown = generate())
click Refresh → doc.Refresh() now
click "Bus Sequence" → ShowBusSequence → SeqThread[] from channel → BusSequenceGenerator.Build → md → …
```

---

## 6. Error handling & plan-time verification

- Generators are total: empty/degenerate input yields a valid, non-empty diagram with a placeholder
  note; ids are sanitized so no agent prefix can produce invalid mermaid.
- Live regeneration is debounced (~500 ms) and marshalled to the UI thread; the watcher is
  unsubscribed on dispose.
- **Plan-time verification:**
  1. **RESOLVED during design:** Naiad's Avalonia render path (`AvaloniaNativeDiagramRendererPlugin` →
     `FlowchartCanvas`, `FlowchartMarkerPrefix`/`FlowchartLayouts`) renders **only Flowchart** diagrams
     on the desktop surface. `DiagramType` includes `Sequence`, but it does not render natively.
     Therefore **both** diagrams are mermaid flowcharts (`graph TD` for the map, `graph LR` for the bus).
     No `sequenceDiagram` is emitted.
  2. Confirm `LucidMarkdownView` renders **inline** markdown set via the `Markdown` property with no
     `SourcePath` (Naiad blocks are self-contained; the existing `MarkdownDocumentView` binds `Markdown`,
     so this should hold) — verify in the first App task and record.

---

## 7. Testing

- **Core (pure):** `SystemMapGenerator` — a node per agent, a parent→child edge per child, a fenced
  `graph TD` block, deterministic order, empty-safe placeholder; sanitizes prefixes. `BusSequenceGenerator`
  — a `graph LR` flowchart block, a node per distinct sender, a labelled edge per consecutive-distinct-
  sender pair per thread ordered by `When`, single-sender "awaiting" case, empty-safe placeholder.
- **App:** `MarkdownDocumentViewModel.FromMarkdown` sets `Markdown` with empty `SourcePath`;
  `ShowSystemMap`/`ShowBusSequence` open a `DiagramDocumentViewModel` whose `Markdown` contains
  `` ```mermaid `` and the expected diagram type; `Refresh` changes `Markdown` after a simulated fleet
  change; a `Live` diagram regenerates on a `Panes` change (debounce driven deterministically via a test
  seam), a non-Live one does not.
- Naiad bitmap rendering is headless-limited — tests assert the generated markdown/text, not the diagram
  image.

---

## 8. Resolved decisions

- **Freshness:** on-demand snapshot + a per-diagram **Live** toggle (debounced ~500 ms auto-refresh).
- **Diagrams:** both **System Map** (`graph TD`) and **Bus Sequence** (`graph LR` flowchart) this
  slice — both flowcharts, because only flowcharts render on Naiad's Avalonia surface (§6).
- **Render path:** generated mermaid markdown opened as a document via the existing
  `OpenMarkdownDocument`/`LucidMarkdownView` path; a `FromMarkdown` factory avoids temp files.
- **Buttons:** in the Documents panel; Refresh + Live controls on the diagram document.
