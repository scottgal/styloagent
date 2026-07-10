# Signal Bus (attention-first) + Document Library + `Mostlylucid.LucidView.Markdown` — Design

**Status:** Design — pending approval
**Date:** 2026-07-10
**Author:** Styloagent

---

## 1. Goal

Two user-facing cockpit features, plus the reusable rendering foundation they need:

1. **Signal Bus → attention-first.** Reorganize the bus so "what needs attention" is
   glanceable: a pinned *Needs attention* group, then *Recent*, then a collapsed
   *Archive* — one row per thread, with a status glyph, colour-coded participants, and
   relative time.
2. **Document Library.** A panel listing markdown from the repo/worktree **and** the
   channel (PROTOCOL.md, agent context, launch prompts), grouped by source. Clicking a
   doc opens it as a **rendered** markdown document — with lucidVIEW's full presentation
   including **real Naiad-rendered diagrams** (flowcharts, mermaid).
3. **`Mostlylucid.LucidView.Markdown`** — extract lucidVIEW's markdown *render logic*
   (control + presentation + Naiad diagram plugins) into a reusable, published package so
   we don't reinvent it, and so it becomes the cockpit's **diagram/markdown visualization
   substrate** (later: C4 layers, bus sequence diagrams, CRC collaborator graphs).

**North star (the operator's, verbatim intent):** *"it's all about coordination & knowing
what needs attention."*

---

## 2. Scope

**In scope**
- Phase 1: extract + publish `Mostlylucid.LucidView.Markdown` (in the `lucidview` repo),
  publishing the unpublished dependency cascade it needs.
- Phase 2 (styloagent): Bus attention-first rework; Document Library panel that renders
  docs via the new package.

**Out of scope (enabled by this work, designed later)**
- C4 "layers" view generator (codebase shape → C4 diagram → rendered).
- Bus **sequence-diagram** generator (live threads → `sequenceDiagram` → rendered).
- CRC responsibility cards + collaborator graphs (Theme 3).
- Top bar / tiling controls (Theme 1), overview-agent + MCP (Theme 4), attention-routing
  auto-focus (Theme 5).

These consume the same renderer; nothing here blocks them, and none are built here.

---

## 3. Phase 1 — `Mostlylucid.LucidView.Markdown` (lucidview repo)

### 3.1 The render seam (established from the code)

lucidVIEW's viewer renders markdown with **`LiveMarkdown.Avalonia.MarkdownRenderer`**. Its
added value on top of the raw control is:

- **Presentation** — fonts/theme/layout applied to the renderer (via `MarkdownViewer`'s
  `AppStyles.axaml` + code-behind styles targeting `MarkdownRenderer` descendants).
- **Diagrams** — `MarkdownService` parses fenced diagram/flowchart blocks and computes
  **Naiad flowchart layouts**; `DiagramRendererPluginHost` + `AvaloniaNativeDiagramRendererPlugin`
  replace diagram markers in the rendered tree with real `FlowchartCanvas` / `DiagramCanvas`
  controls.
- **Image caching** — `ImageCacheService` (+ `Mostlylucid.ImageSharp.Svg` for SVG).

### 3.2 What moves into the package

- **Controls:** `DiagramCanvas`, `FlowchartCanvas`.
- **Plugins:** `IDiagramRendererPlugin`, `DiagramRendererPluginHost`,
  `AvaloniaNativeDiagramRendererPlugin`.
- **Services:** `MarkdownService` (parse + Naiad flowchart layout + metadata extraction),
  `ImageCacheService`.
- **Styles:** the markdown-relevant subset of `AppStyles.axaml` (fonts/spacing/theme for
  the renderer), packaged as a control-library style the consumer includes.
- **New control — `LucidMarkdownView` (UserControl):** the single reusable entry point.
  It composes `MarkdownRenderer` + `MarkdownService` + `DiagramRendererPluginHost` and
  applies the presentation styles. This is the piece `MarkdownViewer.MainWindow` inlines
  today; we lift it into a self-contained control.

### 3.3 What stays behind (app-only, NOT extracted)

Windows/dialogs, PDF export (`PdfExportService`/QuestPDF), HTML→markdown
(`HtmlToMarkdownService`, StyloExtract), search, session history, navigation, print,
settings. `MarkdownViewer` keeps working by consuming the new package (its `MainWindow`
swaps its inline renderer for `LucidMarkdownView`) — this both dogfoods and de-risks the
extraction.

### 3.4 Public API

```csharp
namespace Mostlylucid.LucidView.Markdown;

public class LucidMarkdownView : UserControl
{
    // Markdown source. Setting it (re)renders, incl. diagrams.
    public static readonly StyledProperty<string?> MarkdownProperty;
    public string? Markdown { get; set; }

    // Base path for resolving relative images/links (defaults to temp).
    public static readonly StyledProperty<string?> SourcePathProperty;
    public string? SourcePath { get; set; }

    // Raised when a link inside the rendered doc is clicked.
    public event EventHandler<LucidLinkClickEventArgs>? LinkClicked;
}
```

Setting `Markdown` renders lucidVIEW-styled markdown with real Naiad diagrams. No app
services required by the consumer.

### 3.5 Dependencies + the publish cascade

Package references (all must be *published* for a clean NuGet package):

| Dependency | Status | Action |
|---|---|---|
| `LiveMarkdown.Avalonia` | published (1.3.x) | Use published. **Wrinkle:** `MarkdownViewer` currently project-refs a *fork* for an `<img>` width/height patch. Plan resolves: prefer the published package; if the patch is required for our docs, publish the fork (or pin a version that carries it). |
| `LiveMarkdown.Avalonia.Mermaid` | published (2.2.0) | Include if the plugin path needs it; otherwise Naiad covers diagrams. Verify in plan. |
| `Naiad` (core) | published (1.3.1) | Use published. |
| `Mostlylucid.ImageSharp.Svg` | **NOT published** | **Publish first** (it already has `PackageId`/packable metadata). |
| `Naiad.Surfaces.Skia` | **NOT published** | Verify whether the diagram plugin path needs it (MarkdownViewer only project-refs *core* `Naiad`). **Publish only if required.** |
| `SkiaSharp` (+ native assets) | published | Use published. |

**Publishing:** add a tag-triggered GitHub Actions workflow mirroring
`nuget-uitesting.yml` (OIDC trusted publishing, user `mostlylucid`), trigger tag
`lucidview-markdown-v*`. Initial version **1.0.0**. Publish `Mostlylucid.ImageSharp.Svg`
(and `Naiad.Surfaces.Skia` if needed) first, then `Mostlylucid.LucidView.Markdown`.

### 3.6 Phase 1 verification

- A headless render test in the lucidview package's test project: host `LucidMarkdownView`
  with sample markdown (heading, code fence, a `mermaid`/flowchart block), use
  `Mostlylucid.Avalonia.UITesting` `HeadlessRender.SettleAsync` + `ScreenshotCapture`, and
  assert (a) rendered text pixels are present and (b) a `FlowchartCanvas`/`DiagramCanvas`
  materializes for the diagram block.
- `MarkdownViewer` still builds + runs against the extracted package (dogfood).

---

## 4. Phase 2 — styloagent Bus + Document Library

### 4.1 Signal Bus — attention-first

**Backend already present:** `ChannelProjection` returns `BusThread`s (slug + ordered
messages + participant prefixes), already ordered by recency, and computes
`BusMessageState.Replied`. We add one **pure** classifier and rework the view-model.

**`BusThreadClassifier` (Core, pure, unit-tested):**

```csharp
public enum BusThreadSection { Attention, Recent, Archive }

public sealed record BusThreadView(
    BusThread Thread,
    BusThreadSection Section,
    string Glyph,
    string Subject,          // prettified slug
    DateTimeOffset? LastActivity);

public static class BusThreadClassifier
{
    public static BusThreadView Classify(BusThread thread);
}
```

Rules (message-derived only, so the classifier stays pure/testable):
- **Archive** — every message in the thread is `State == Archived`.
- **Attention** — not archived AND the thread has an inbox/broadcast message that is
  `State == New` (i.e., awaiting a reply / unreplied).
- **Recent** — otherwise (active but not awaiting a reply).

Glyph precedence (Archive section forces `▤`):
`●` unreplied inbox → `↩` replied → `◆` broadcast → `○` other.

> Note: *agent* state (⚠ `WaitingForHuman`) is surfaced in the **roster** (§4.4 hook
> channel, already built) and by attention-routing (Theme 5). The bus's "attention" is
> **message-derived** (unreplied) to keep this classifier pure. The two are complementary.

**`BusViewModel` rework:** replace the `CurrentMessages`/`ArchivedMessages` split with
three `ObservableCollection<BusThreadItem>`: `AttentionThreads`, `RecentThreads`,
`ArchivedThreads`. `UpdateThreads()` maps `ChannelProjection` threads through the
classifier and buckets them. `BusThreadItem` exposes: `Glyph`, `Subject`,
`ParticipantsDisplay` (`from → to`), per-participant `ColorHex`
(`PresentationStore.DefaultColorFor` — shared key, matches roster/terminal),
`RelativeTime`, `IsExpanded`, and `Messages` (for inline expansion). Existing
FileSystemWatcher/debounce/reload plumbing is reused unchanged.

**`BusView` rework:** three sections (`Border` header + count + collapse toggle), each an
`ItemsControl` of thread rows. A row: status glyph, colour stripe/chips for participants,
subject, relative time. Click toggles `IsExpanded` → shows the thread's messages inline.
(ItemsControl materializes headless now that `TestApp` loads the theme.)

### 4.2 Document Library

**`DocLibraryReader` (Core, pure, unit-tested):**

```csharp
public enum DocSource { Repo, Channel }

public sealed record DocEntry(
    string Title, string FullPath, DocSource Source, string RelativePath);

public sealed class DocLibraryReader
{
    // Enumerate *.md under repoRoot (excluding bin/obj/.git/node_modules) and under
    // channelRoot (PROTOCOL.md, saved-context/*.md, launch-prompts/*.md). Returns
    // entries grouped-ready (Source + RelativePath). Never throws; skips unreadable.
    public IReadOnlyList<DocEntry> Read(string? repoRoot, string? channelRoot);
}
```

**`DocLibraryViewModel` (App):** holds `DocEntry`s grouped by `Source`; exposes
`OpenDocCommand(DocEntry)`. Opening creates a `MarkdownDocumentViewModel` and adds it as a
**center dock `Document`** via the existing `StyloagentDockFactory` (activated + focused),
so a doc tabs/tiles/floats beside a terminal. Refreshes its list on file changes
(FileSystemWatcher on the doc roots).

**`MarkdownDocumentViewModel` (App):** `Title`, `FullPath`, `Markdown` (loaded text);
reloads on file change (live). Rendered by `MarkdownDocumentView`, which hosts a
`LucidMarkdownView` bound to `Markdown` with `SourcePath` = the doc's directory.

**`DocLibraryView` (App):** a tree/list grouped by source (`▾ repo`, `▾ channel`), each
entry a button invoking `OpenDocCommand`.

### 4.3 Placement

`MainWindow` right column becomes a **`TabControl`**: `Signal Bus | Documents`. Roster stays
left; the center `DockControl` hosts terminals **and** opened markdown documents. Opening a
doc adds a dock `Document` whose `Context` is a `MarkdownDocumentViewModel`; an
app-level `DataTemplate` maps it to `MarkdownDocumentView`.

### 4.4 Dependency

styloagent's App project adds a `PackageReference` to `Mostlylucid.LucidView.Markdown`
(published in Phase 1). Verify it targets Avalonia 11.3.x (styloagent's version).

---

## 5. Data flow

- **Bus:** channel files → `ChannelProjection` (threads, recency-ordered, Replied computed)
  → `BusThreadClassifier` → `BusViewModel` (Attention/Recent/Archive) → `BusView`.
- **Docs (list):** repo + channel roots → `DocLibraryReader` → `DocLibraryViewModel`
  (grouped) → `DocLibraryView`.
- **Docs (open):** `OpenDocCommand` → `MarkdownDocumentViewModel` (loads file) → dock
  `Document` → `MarkdownDocumentView` → `LucidMarkdownView` renders (text + Naiad diagrams).

---

## 6. Testing

**Phase 1 (lucidview):**
- `LucidMarkdownView` headless render test (text pixels present; diagram control
  materializes for a mermaid/flowchart block) via UITesting `SettleAsync` + screenshot.
- `MarkdownViewer` builds/runs against the extracted package.

**Phase 2 (styloagent):**
- *Core:* `BusThreadClassifier` (Attention/Recent/Archive bucketing + glyph precedence
  across inbox/reply/broadcast/archived fixtures); `DocLibraryReader` (enumeration,
  source classification, exclusions, never-throws).
- *App VM:* `BusViewModel` buckets threads into the three sections; `DocLibraryViewModel`
  groups by source and `OpenDoc` adds a dock `Document`; `MarkdownDocumentViewModel` loads
  a file + refreshes on change.
- *UITests:* right panel renders Bus|Docs tabs; bus renders the three sections with thread
  rows (ItemsControl materializes with the theme); doc library tree renders; opening a doc
  adds a `MarkdownDocumentView`/`LucidMarkdownView` document (settle + screenshot).

---

## 7. Files (map)

**Phase 1 — new project `lucidview/src/Mostlylucid.LucidView.Markdown/`:**
`Mostlylucid.LucidView.Markdown.csproj` (packable), `LucidMarkdownView.axaml(.cs)`,
`Controls/DiagramCanvas.cs`, `Controls/FlowchartCanvas.cs`, `Plugins/*` (3 files),
`Services/MarkdownService.cs`, `Services/ImageCacheService.cs`,
`Styles/LucidMarkdownStyles.axaml`, `LucidLinkClickEventArgs.cs`.
Modify: `MarkdownViewer` to consume the package; publish workflow
`.github/workflows/nuget-lucidview-markdown.yml`; publish `Mostlylucid.ImageSharp.Svg`
(+ `Naiad.Surfaces.Skia` if needed).

**Phase 2 — styloagent:**
Create: `src/Styloagent.Core/Channel/BusThreadClassifier.cs`,
`src/Styloagent.Core/Docs/DocLibraryReader.cs` (+ `DocEntry.cs`, `DocSource.cs`);
`src/Styloagent.App/ViewModels/DocLibraryViewModel.cs`,
`src/Styloagent.App/ViewModels/MarkdownDocumentViewModel.cs`;
`src/Styloagent.App/Views/DocLibraryView.axaml(.cs)`,
`src/Styloagent.App/Views/MarkdownDocumentView.axaml(.cs)`.
Modify: `BusViewModel.cs` (sections), `BusView.axaml` (3 sections + rows),
`MainWindow.axaml` (right-panel `TabControl`), `App.axaml` (DataTemplate for
`MarkdownDocumentViewModel`), `Styloagent.App.csproj` (package ref).

---

## 8. Resolved decisions

- Bus organization: **attention-first** (Needs attention / Recent / Archive).
- Doc scope + open: **repo + channel markdown, rendered** as dock documents.
- Markdown rendering: **extract full-fidelity `Mostlylucid.LucidView.Markdown` (Naiad
  diagrams), published**; publish the dependency cascade.
- Bus thread click: **expand inline**.
- Placement: **right-panel Bus|Docs tabs; docs open as center dock documents**.

## 9. Build order

1. Publish `Mostlylucid.ImageSharp.Svg` (+ `Naiad.Surfaces.Skia` if the plugin path needs
   it).
2. Extract + publish `Mostlylucid.LucidView.Markdown`; migrate `MarkdownViewer` onto it.
3. styloagent **Bus attention-first** (independent — can land before/parallel to 1–2).
4. styloagent **Document Library** consuming the package.
</content>
