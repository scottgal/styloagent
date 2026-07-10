# Diagram-driven Cockpit Views Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate mermaid **flowchart** markdown from live fleet + bus state (System Map + Bus Sequence) and open it as a rendered document, with on-demand generation and a debounced Live toggle.

**Architecture:** Two pure Core generators emit `graph TD` / `graph LR` markdown. A `DiagramDocumentViewModel : MarkdownDocumentViewModel` (opened via the existing `OpenMarkdownDocument` → `LucidMarkdownView`/Naiad path) carries a Refresh + Live toggle; the shell regenerates Live diagrams on fleet/bus changes, debounced.

**Tech Stack:** .NET 10 · Avalonia 11.3 · `Mostlylucid.LucidView.Markdown` (Naiad) · CommunityToolkit.Mvvm · xUnit.

## Global Constraints

- **Both diagrams are mermaid FLOWCHARTS** — Naiad's Avalonia render surface (`AvaloniaNativeDiagramRendererPlugin` → `FlowchartCanvas`) renders ONLY flowcharts. System Map = `graph TD`; Bus Sequence = `graph LR`. Do NOT emit `sequenceDiagram`.
- Generators are pure, deterministic, and TOTAL (empty/degenerate input → a valid non-empty flowchart with a placeholder node; never throw). Node ids sanitized to `[A-Za-z0-9_]` (prefix an `n` if it starts with a digit); labels escape `"`.
- Reuse the existing render path: `MainWindowViewModel.OpenMarkdownDocument(MarkdownDocumentViewModel)`, `MarkdownDocumentView` → `LucidMarkdownView` binding `Markdown`.
- Fleet source: `AgentPaneViewModel.{Prefix, ParentPrefix, Responsibility, HookStateText}`. Bus source: the channel messages (`BusMessage.From` + `Timestamp`, grouped by `Slug`) — ground the exact accessor against `BusViewModel`/`ChannelProjection` in Task 4.
- The repo's `.editorconfig` treats many CA rules as ERRORS — run `dotnet build` and fix every `error CA####`.
- All doc/queue mutations on the UI thread; Live regeneration debounced (~500 ms). No real `claude` in tests; Naiad bitmap rendering is headless-limited so tests assert the generated markdown/text, not the diagram image.
- Commit each task with `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "..."` ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work on `main`; do not branch.

---

### Task 1: SystemMapGenerator (pure, graph TD)

**Files:**
- Create: `src/Styloagent.Core/Diagrams/SystemMapGenerator.cs`
- Test: `tests/Styloagent.Core.Tests/SystemMapGeneratorTests.cs`

**Interfaces:**
- Produces: `sealed record FleetNode(string Prefix, string? ParentPrefix, string Responsibility, string State)`; `static class SystemMapGenerator { string Build(IEnumerable<FleetNode> nodes); }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/SystemMapGeneratorTests.cs`:

```csharp
using Styloagent.Core.Diagrams;
using Xunit;

namespace Styloagent.Core.Tests;

public class SystemMapGeneratorTests
{
    [Fact]
    public void Build_emits_a_flowchart_with_nodes_and_parent_edges()
    {
        var md = SystemMapGenerator.Build(new[]
        {
            new FleetNode("overview-", null, "the architect", "working"),
            new FleetNode("foss-", "overview-", "owns FOSS", "needs you"),
        });

        Assert.Contains("```mermaid", md);
        Assert.Contains("graph TD", md);
        Assert.Contains("overview<br/>the architect", md);       // node label
        Assert.Contains("foss<br/>owns FOSS", md);
        Assert.Contains("--> ", md);                              // an edge exists
        Assert.Contains("class ", md);                            // state styling applied
    }

    [Fact]
    public void Build_is_empty_safe()
    {
        var md = SystemMapGenerator.Build(Array.Empty<FleetNode>());
        Assert.Contains("graph TD", md);
        Assert.Contains("no agents yet", md);
    }

    [Fact]
    public void Build_sanitizes_ids_but_keeps_prefix_in_the_label()
    {
        var md = SystemMapGenerator.Build(new[] { new FleetNode("foss-", null, "r", "working") });
        Assert.Contains("foss[\"foss-<br/>r\"]", md);             // id 'foss', label 'foss-'
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "SystemMapGeneratorTests" --nologo`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement**

Create `src/Styloagent.Core/Diagrams/SystemMapGenerator.cs`:

```csharp
using System.Text;

namespace Styloagent.Core.Diagrams;

public sealed record FleetNode(string Prefix, string? ParentPrefix, string Responsibility, string State);

/// <summary>Renders the agent fleet tree as a mermaid flowchart (graph TD). Pure, total, deterministic.</summary>
public static class SystemMapGenerator
{
    public static string Build(IEnumerable<FleetNode> nodes)
    {
        var list = nodes.OrderBy(n => n.Prefix, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("# System Map\n\n```mermaid\ngraph TD\n");
        if (list.Count == 0)
        {
            sb.Append("    empty[\"no agents yet\"]\n```\n");
            return sb.ToString();
        }
        foreach (var n in list)
            sb.Append($"    {Id(n.Prefix)}[\"{Escape(n.Prefix)}<br/>{Escape(n.Responsibility)}\"]\n");
        foreach (var n in list.Where(n => !string.IsNullOrWhiteSpace(n.ParentPrefix)))
            sb.Append($"    {Id(n.ParentPrefix!)} --> {Id(n.Prefix)}\n");
        sb.Append("    classDef working fill:#12351f,stroke:#3fb950,color:#e6edf3;\n");
        sb.Append("    classDef idle fill:#21262d,stroke:#8b949e,color:#e6edf3;\n");
        sb.Append("    classDef needsYou fill:#3a2a00,stroke:#e5a05a,color:#e6edf3;\n");
        sb.Append("    classDef exited fill:#3d1417,stroke:#f85149,color:#e6edf3;\n");
        foreach (var n in list)
        {
            var cls = StateClass(n.State);
            if (cls is not null) sb.Append($"    class {Id(n.Prefix)} {cls};\n");
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    internal static string Id(string prefix)
    {
        var chars = prefix.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray();
        var id = new string(chars);
        if (id.Length == 0) id = "n";
        if (char.IsAsciiDigit(id[0])) id = "n" + id;
        return id;
    }

    internal static string Escape(string s) => s.Replace("\"", "'").Replace("\n", " ");

    private static string? StateClass(string state) => state switch
    {
        "working" => "working",
        "idle" => "idle",
        "needs you" => "needsYou",
        "exited" => "exited",
        _ => null,
    };
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "SystemMapGeneratorTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.Core --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Diagrams/SystemMapGenerator.cs tests/Styloagent.Core.Tests/SystemMapGeneratorTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(diagrams): SystemMapGenerator — fleet tree as a mermaid flowchart

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: BusSequenceGenerator (pure, graph LR)

**Files:**
- Create: `src/Styloagent.Core/Diagrams/BusSequenceGenerator.cs`
- Test: `tests/Styloagent.Core.Tests/BusSequenceGeneratorTests.cs`

**Interfaces:**
- Produces: `sealed record SeqMessage(string From, DateTimeOffset? When)`; `sealed record SeqThread(string Slug, IReadOnlyList<SeqMessage> Messages)`; `static class BusSequenceGenerator { string Build(IEnumerable<SeqThread> threads); }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/BusSequenceGeneratorTests.cs`:

```csharp
using Styloagent.Core.Diagrams;
using Xunit;

namespace Styloagent.Core.Tests;

public class BusSequenceGeneratorTests
{
    private static SeqMessage M(string from, int minAgo)
        => new(from, DateTimeOffset.UtcNow.AddMinutes(-minAgo));

    [Fact]
    public void Build_emits_a_flowchart_edge_between_consecutive_senders()
    {
        var md = BusSequenceGenerator.Build(new[]
        {
            new SeqThread("release-cut", new[] { M("deploy-", 5), M("foss-", 4) }),
        });

        Assert.Contains("```mermaid", md);
        Assert.Contains("graph LR", md);
        Assert.Contains("deploy[\"deploy-\"]", md);
        Assert.Contains("foss[\"foss-\"]", md);
        Assert.Contains("deploy -->|release-cut| foss", md);
    }

    [Fact]
    public void Single_sender_thread_shows_awaiting()
    {
        var md = BusSequenceGenerator.Build(new[]
        {
            new SeqThread("ping", new[] { M("mae-", 2) }),
        });
        Assert.Contains("awaiting reply", md);
        Assert.Contains("mae- -->".Replace("mae-", "mae"), md);   // edge from mae to the awaiting node
    }

    [Fact]
    public void Build_is_empty_safe()
    {
        var md = BusSequenceGenerator.Build(Array.Empty<SeqThread>());
        Assert.Contains("graph LR", md);
        Assert.Contains("no bus activity yet", md);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "BusSequenceGeneratorTests" --nologo`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement**

Create `src/Styloagent.Core/Diagrams/BusSequenceGenerator.cs`:

```csharp
using System.Text;

namespace Styloagent.Core.Diagrams;

public sealed record SeqMessage(string From, DateTimeOffset? When);
public sealed record SeqThread(string Slug, IReadOnlyList<SeqMessage> Messages);

/// <summary>Renders bus threads as a mermaid flowchart (graph LR) of message flow. Pure, total.</summary>
public static class BusSequenceGenerator
{
    public static string Build(IEnumerable<SeqThread> threads)
    {
        var list = threads.ToList();
        var sb = new StringBuilder();
        sb.Append("# Bus Sequence\n\n```mermaid\ngraph LR\n");

        var senders = new List<string>();
        foreach (var t in list)
            foreach (var m in t.Messages)
                if (!string.IsNullOrWhiteSpace(m.From) && !senders.Contains(m.From)) senders.Add(m.From);

        if (senders.Count == 0)
        {
            sb.Append("    empty[\"no bus activity yet\"]\n```\n");
            return sb.ToString();
        }

        foreach (var s in senders)
            sb.Append($"    {SystemMapGenerator.Id(s)}[\"{SystemMapGenerator.Escape(s)}\"]\n");

        int awaiting = 0;
        var orderedThreads = list.OrderBy(t =>
            t.Messages.Select(m => m.When ?? DateTimeOffset.MaxValue).DefaultIfEmpty(DateTimeOffset.MaxValue).Min());
        foreach (var t in orderedThreads)
        {
            var chain = new List<string>();
            foreach (var m in t.Messages
                         .Where(m => !string.IsNullOrWhiteSpace(m.From))
                         .OrderBy(m => m.When ?? DateTimeOffset.MaxValue))
                if (chain.Count == 0 || chain[^1] != m.From) chain.Add(m.From);

            if (chain.Count == 0) continue;
            if (chain.Count == 1)
            {
                var aid = $"await{awaiting++}";
                sb.Append($"    {aid}[\"{SystemMapGenerator.Escape(chain[0])}: {SystemMapGenerator.Escape(t.Slug)} (awaiting reply)\"]\n");
                sb.Append($"    {SystemMapGenerator.Id(chain[0])} --> {aid}\n");
            }
            else
            {
                for (int i = 0; i + 1 < chain.Count; i++)
                    sb.Append($"    {SystemMapGenerator.Id(chain[i])} -->|{EdgeLabel(t.Slug)}| {SystemMapGenerator.Id(chain[i + 1])}\n");
            }
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    // Mermaid edge labels can't contain '|' or '"'; keep it simple.
    private static string EdgeLabel(string slug) => slug.Replace("|", "/").Replace("\"", "'");
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "BusSequenceGeneratorTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.Core --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Diagrams/BusSequenceGenerator.cs tests/Styloagent.Core.Tests/BusSequenceGeneratorTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(diagrams): BusSequenceGenerator — bus threads as a mermaid flowchart

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: FromMarkdown + DiagramDocumentViewModel

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MarkdownDocumentViewModel.cs`
- Create: `src/Styloagent.App/ViewModels/DiagramDocumentViewModel.cs`
- Test: `tests/Styloagent.App.Tests/DiagramDocumentTests.cs`

**Interfaces:**
- Consumes: generators (Tasks 1-2); the existing `MarkdownDocumentViewModel` (read it — it exposes a `Markdown` property + a `(title, fullPath)` ctor; confirm the property/field names).
- Produces: `static MarkdownDocumentViewModel MarkdownDocumentViewModel.FromMarkdown(string title, string markdown)` (sets `Markdown`, empty `SourcePath`, no file read); `enum DiagramKind { SystemMap, BusSequence }`; `sealed partial class DiagramDocumentViewModel : MarkdownDocumentViewModel` with ctor `(string title, DiagramKind kind, Func<string> generate)` (sets initial `Markdown = generate()`), `DiagramKind Kind { get; }`, `[ObservableProperty] bool _live`, `[RelayCommand] void Refresh()` (sets `Markdown = _generate()`).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/DiagramDocumentTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

public class DiagramDocumentTests
{
    [Fact]
    public void FromMarkdown_sets_markdown_without_a_file()
    {
        var doc = MarkdownDocumentViewModel.FromMarkdown("System Map", "# hi\n\nbody");
        Assert.Equal("System Map", doc.Title);
        Assert.Contains("body", doc.Markdown);
        Assert.True(string.IsNullOrEmpty(doc.SourcePath));
    }

    [Fact]
    public void Diagram_doc_refresh_regenerates_markdown()
    {
        int calls = 0;
        var doc = new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap, () => $"gen {++calls}");
        Assert.Equal("gen 1", doc.Markdown);        // generated at construction
        doc.RefreshCommand.Execute(null);
        Assert.Equal("gen 2", doc.Markdown);        // regenerated
        Assert.Equal(DiagramKind.SystemMap, doc.Kind);
    }
}
```

(If `MarkdownDocumentViewModel` exposes `Markdown`/`SourcePath`/`Title` under different names, adapt the
assertions to the REAL property names — read the file first.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "DiagramDocumentTests" --nologo`
Expected: FAIL — `FromMarkdown`/`DiagramDocumentViewModel` missing.

- [ ] **Step 3: Implement**

Read `MarkdownDocumentViewModel.cs`. Add a factory that sets the content directly (match the real
property names — this assumes a settable `Markdown` and `SourcePath`):

```csharp
    /// <summary>Opens generated markdown content directly (no file read).</summary>
    public static MarkdownDocumentViewModel FromMarkdown(string title, string markdown)
    {
        var vm = new MarkdownDocumentViewModel(title, string.Empty);  // or the real minimal ctor
        vm.Markdown = markdown;
        vm.SourcePath = string.Empty;
        return vm;
    }
```

If the existing ctor requires a real file path (reads on construction) and can't take an empty path,
add a `protected`/`internal` parameterless-ish ctor or a `protected MarkdownDocumentViewModel(string title)`
that sets `Title` without reading a file, and have `FromMarkdown` + `DiagramDocumentViewModel` use it.
Keep the file-path ctor's behaviour unchanged. Note the approach in the report.

Create `src/Styloagent.App/ViewModels/DiagramDocumentViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Styloagent.App.ViewModels;

public enum DiagramKind { SystemMap, BusSequence }

/// <summary>A generated diagram document (Refresh + Live toggle) that renders through LucidMarkdownView.</summary>
public sealed partial class DiagramDocumentViewModel : MarkdownDocumentViewModel
{
    private readonly Func<string> _generate;
    public DiagramKind Kind { get; }

    [ObservableProperty]
    private bool _live;

    public DiagramDocumentViewModel(string title, DiagramKind kind, Func<string> generate)
        : base(title)                    // use the no-file base ctor added above
    {
        _generate = generate;
        Kind = kind;
        Markdown = _generate();
    }

    [RelayCommand]
    public void Refresh() => Markdown = _generate();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "DiagramDocumentTests" --nologo`
Expected: PASS. Then `dotnet test tests/Styloagent.App.Tests --nologo` (no regression). Then
`dotnet build src/Styloagent.App --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/MarkdownDocumentViewModel.cs src/Styloagent.App/ViewModels/DiagramDocumentViewModel.cs tests/Styloagent.App.Tests/DiagramDocumentTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(diagrams): FromMarkdown factory + DiagramDocumentViewModel (Refresh + Live)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Shell commands + bus mapping + debounced Live watcher

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/DiagramCommandsTests.cs`

**Interfaces:**
- Consumes: `SystemMapGenerator`/`FleetNode` (Task 1), `BusSequenceGenerator`/`SeqThread`/`SeqMessage` (Task 2), `DiagramDocumentViewModel` (Task 3), existing `OpenMarkdownDocument`, `Panes`, and the channel/bus data.
- Produces on `MainWindowViewModel`: `[RelayCommand] void ShowSystemMap()`; `[RelayCommand] void ShowBusSequence()`; internal `IReadOnlyList<FleetNode> BuildFleetNodes()`; internal `IReadOnlyList<SeqThread> BuildBusThreads()`; a debounced watcher that calls `Refresh()` on every tracked diagram whose `Live` is true when `Panes`/bus change; diagrams tracked in a list and removed when their document closes.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/DiagramCommandsTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

public class DiagramCommandsTests
{
    [Fact]
    public async Task ShowSystemMap_opens_a_flowchart_diagram_doc()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.ShowSystemMapCommand.Execute(null);

            var doc = vm.OpenDiagramsForTest().LastOrDefault();
            Assert.NotNull(doc);
            Assert.Equal(DiagramKind.SystemMap, doc!.Kind);
            Assert.Contains("graph TD", doc.Markdown);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Live_diagram_regenerates_on_a_fleet_change()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.ShowSystemMapCommand.Execute(null);
            var doc = vm.OpenDiagramsForTest().Last();
            doc.Live = true;
            var before = doc.Markdown;

            vm.AddAgentCommand.Execute(null);        // fleet changes → a new pane
            vm.RegenerateLiveDiagramsForTest();      // deterministic stand-in for the debounce tick

            Assert.NotEqual(before, doc.Markdown);   // regenerated with the new agent
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

(Adapt `AddAgentCommand` to the real command name if different. `OpenDiagramsForTest()` /
`RegenerateLiveDiagramsForTest()` are internal test seams added below.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "DiagramCommandsTests" --nologo`
Expected: FAIL — commands/seams missing.

- [ ] **Step 3: Implement**

Read `MainWindowViewModel.cs` and how the bus data is reachable (`_busViewModel` and/or a
`ChannelProjection`/messages accessor). Add:

```csharp
    private readonly List<DiagramDocumentViewModel> _openDiagrams = new();
    internal IReadOnlyList<DiagramDocumentViewModel> OpenDiagramsForTest() => _openDiagrams;

    internal IReadOnlyList<FleetNode> BuildFleetNodes()
        => Panes.Select(p => new FleetNode(p.Prefix, p.ParentPrefix, p.Responsibility, p.HookStateText ?? "")).ToList();

    // Ground this against the real bus/channel accessor — group messages by Slug into SeqThread/SeqMessage.
    internal IReadOnlyList<SeqThread> BuildBusThreads() { /* map from the channel messages */ }

    [RelayCommand]
    private void ShowSystemMap()
    {
        var doc = new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap,
            () => SystemMapGenerator.Build(BuildFleetNodes()));
        _openDiagrams.Add(doc);
        OpenMarkdownDocument(doc);
    }

    [RelayCommand]
    private void ShowBusSequence()
    {
        var doc = new DiagramDocumentViewModel("Bus Sequence", DiagramKind.BusSequence,
            () => BusSequenceGenerator.Build(BuildBusThreads()));
        _openDiagrams.Add(doc);
        OpenMarkdownDocument(doc);
    }

    internal void RegenerateLiveDiagramsForTest() => RegenerateLiveDiagrams();
    private void RegenerateLiveDiagrams()
    {
        foreach (var d in _openDiagrams)
            if (d.Live) d.Refresh();
    }
```

Wire a debounced trigger: arm a `DispatcherTimer` (~500 ms, single-shot reset) on `Panes.CollectionChanged`
and on the bus refresh signal (subscribe to `_busViewModel`'s update/PropertyChanged, or wherever the
channel projection notifies). On tick → `RegenerateLiveDiagrams()`. Stop the timer in `Dispose()`. If a
bus refresh signal isn't readily available, arming on `Panes.CollectionChanged` is sufficient for this
slice — note the limitation. For `BuildBusThreads`, if the cleanest source is `_busViewModel`'s threads
(`BusThreadItem.Messages` → `BusMessageItem`), map those; if `BusMessageItem` lacks `From`/`Timestamp`,
read from the `ChannelProjection`/`BusMessage` list instead. Ground it and note the source used.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "DiagramCommandsTests" --nologo`
Expected: PASS. Then full `dotnet test tests/Styloagent.App.Tests --nologo`. Then
`dotnet build src/Styloagent.App --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/DiagramCommandsTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(diagrams): ShowSystemMap/ShowBusSequence + debounced Live regeneration

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Views — buttons + Refresh/Live row + render verification

**Files:**
- Modify: `src/Styloagent.App/Views/DocLibraryView.axaml`, `src/Styloagent.App/Views/MarkdownDocumentView.axaml`, `src/Styloagent.App/Views/MarkdownDocumentView.axaml.cs`
- Test: `tests/Styloagent.UITests/DiagramViewTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.{ShowSystemMapCommand, ShowBusSequenceCommand}`; `DiagramDocumentViewModel.{RefreshCommand, Live, Kind}`; existing `MarkdownDocumentView` → `LucidMarkdownView` binding `Markdown`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.UITests/DiagramViewTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class DiagramViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public DiagramViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Diagram_document_renders_its_generated_markdown_text()
    {
        return _fx.DispatchAsync(async () =>
        {
            var doc = new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap,
                () => "# System Map\n\nThe fleet map heading renders.");
            var view = new MarkdownDocumentView { DataContext = doc };
            var window = new Window { Width = 560, Height = 360, Content = view };
            window.Show();

            int Texts() => window.GetVisualDescendants().OfType<TextBlock>().Count();
            for (int i = 0; i < 40 && Texts() < 1; i++) { await HeadlessRender.SettleAsync(window); await Task.Delay(25); }

            Assert.True(Texts() >= 1, "diagram markdown should render into text");
            window.Close();
        });
    }
}
```

(Naiad flowchart bitmaps don't realize headless — this asserts the markdown text renders, which is the
verification that inline `Markdown` flows through `MarkdownDocumentView` → `LucidMarkdownView`.)

- [ ] **Step 2: Run to verify it fails (or passes trivially)**

Run: `dotnet test tests/Styloagent.UITests --filter "DiagramViewTests" --nologo`
If `MarkdownDocumentView` already renders `Markdown`, this may PASS immediately — that's the inline-render
verification from the spec (§6.2). If it FAILS to render inline content, fix `FromMarkdown`/the view
binding until it renders. Record the result.

- [ ] **Step 3: Implement the buttons + Refresh/Live row**

In `src/Styloagent.App/Views/DocLibraryView.axaml`, add to the panel header (bind through the view's
`MainWindowViewModel` DataContext — confirm the DataContext; if the DocLibraryView's DataContext is the
`DocLibraryViewModel`, route the command via `RelativeSource` to the window's `MainWindowViewModel`, or
expose the two commands on `DocLibraryViewModel` as pass-throughs — pick the cleaner and note it):

```xml
    <StackPanel Orientation="Horizontal" Spacing="6">
      <Button Content="System Map" FontSize="10" Padding="6,1" Command="{Binding ShowSystemMapCommand}" />
      <Button Content="Bus Sequence" FontSize="10" Padding="6,1" Command="{Binding ShowBusSequenceCommand}" />
    </StackPanel>
```

In `src/Styloagent.App/Views/MarkdownDocumentView.axaml`, add a top row shown only for diagram docs:

```xml
    <StackPanel Orientation="Horizontal" Spacing="6" Margin="8,4"
                IsVisible="{Binding Kind, Converter={x:Static conv:NotNullConverter.Instance}, FallbackValue=False}">
      <Button Content="⟳ Refresh" FontSize="10" Padding="6,1" Command="{Binding RefreshCommand}" />
      <ToggleButton Content="Live" FontSize="10" Padding="6,1" IsChecked="{Binding Live}" />
    </StackPanel>
```

`Kind` only exists on `DiagramDocumentViewModel`; for a plain `MarkdownDocumentViewModel` the binding
fails closed (row hidden). If a `NotNullConverter` doesn't exist, gate visibility another safe way (e.g.
bind `IsVisible` to a `bool IsDiagram => false` on the base VM, overridden to `true` on
`DiagramDocumentViewModel`) — pick the cleaner and note it. Add `xmlns:conv` if needed.

- [ ] **Step 4: Run tests + build**

Run: `dotnet test tests/Styloagent.UITests --filter "DiagramViewTests" --nologo` → PASS.
Then full `dotnet test --nologo` (whole solution green). Then `dotnet build --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Views/DocLibraryView.axaml src/Styloagent.App/Views/MarkdownDocumentView.axaml src/Styloagent.App/Views/MarkdownDocumentView.axaml.cs tests/Styloagent.UITests/DiagramViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(diagrams): System Map / Bus Sequence buttons + Refresh/Live row

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes / follow-ups (not this plan)

- Click-a-node-to-focus-the-agent; diagram export/save; a bus refresh signal for Live if not wired in Task 4.
- README demo: a System Map screenshot once landed (Naiad flowchart renders in a real GUI, not headless).
- If `IsDiagram`/`Kind` visibility gating proves awkward, a dedicated `DiagramDocumentView` is an alternative.
