# Styloagent Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Styloagent cockpit foundation — validate the stack, then ship a walking skeleton that spawns, dehydrates, and rehydrates ONE owned agent terminal in a dockable Avalonia shell.

**Architecture:** A `Styloagent.Core` class library holds the pure, headless logic (fleet manifest, `AgentSession` state machine, channel-seeding) with no UI dependency. A `Styloagent.Terminal` library owns the PTY (`Porta.Pty`) and renders it with an XTerm.NET-backed Avalonia control. A `Styloagent.App` Avalonia project hosts the Dock shell and view-models. Markdown rendering comes from a `Mostlylucid.LucidView.Markdown` NuGet extracted from lucidVIEW (prerequisite phase, in the lucidview repo). Filesystem/git is the source of truth; Styloagent is a projection/presentation layer that must degrade, never destroy.

**Tech Stack:** .NET 10, Avalonia 11.3, Dock 11.3.x, Porta.Pty, XTerm.NET, VYaml (config), CliWrap (git — later slice), xUnit (tests). Reference: `docs/superpowers/specs/2026-07-09-styloagent-cockpit-design.md`.

## Global Constraints

- **.NET 10**, **Avalonia 11.3** (Dock 11.3.x). **No Native AOT** — plain trimmed self-contained publish.
- **macOS-primary**, cross-platform desirable. Test on macOS arm64.
- **Hard invariant:** filesystem + git are the durable source of truth; Styloagent is projection/delivery/presentation. Closing it must not break the channel. Degrade, never destroy.
- **Own the `Porta.Pty` layer directly**; the terminal *renderer* must be swappable (depend on our own `IPtySession` abstraction, not on the control).
- **Config = YAML via VYaml** (fleet manifest + presentation sidecar). Human-editable.
- **Markdown = `Mostlylucid.LucidView.Markdown`** (extracted lucidVIEW presentation: LiveMarkdown.Avalonia + Naiad mermaid + themes/fonts). No new renderer.
- **Presentation state never mixes into the shared channel files.** Live PTY state is never serialized — restore = re-spawn.
- **Home repo:** `~/RiderProjects/styloagent/`. Extraction work lands in `~/RiderProjects/lucidview/`.
- **Channel root is configurable**; `/tmp/agent-channel` is the current instance, not a hardcoded constant.
- **Commit** after every green step. End commit messages with the `Co-Authored-By` trailer used in this repo.

---

## File Structure

```
~/RiderProjects/styloagent/
├── Styloagent.sln
├── src/
│   ├── Styloagent.Core/                 # headless logic, no UI
│   │   ├── Model/
│   │   │   ├── AgentManifestEntry.cs     # prefix↔repo↔worktree↔prompts↔transport
│   │   │   ├── AgentTransport.cs         # Local | Ssh(host, credRef)
│   │   │   ├── SessionState.cs           # enum
│   │   │   └── AgentPresentation.cs      # cockpit-only: name, colour
│   │   ├── Config/
│   │   │   ├── ManifestStore.cs          # VYaml load/save of the manifest index
│   │   │   └── PresentationStore.cs      # VYaml load/save of the sidecar
│   │   ├── Seeding/
│   │   │   └── ChannelManifestSeeder.cs  # scan channel → manifest entries
│   │   ├── Sessions/
│   │   │   ├── IPtySession.cs            # our swappable PTY abstraction
│   │   │   ├── IPtyLauncher.cs
│   │   │   ├── PtySpawnOptions.cs
│   │   │   └── AgentSession.cs           # the spawn/dehydrate/rehydrate state machine
│   │   └── Abstractions/
│   │       └── IFileWatcher.cs           # observe saved-context change (ack)
│   ├── Styloagent.Terminal/             # PTY + Avalonia render control
│   │   ├── PortaPtySession.cs           # IPtySession over Porta.Pty
│   │   ├── PortaPtyLauncher.cs          # IPtyLauncher
│   │   └── TerminalControl.axaml(.cs)   # XTerm.NET-backed Avalonia control
│   └── Styloagent.App/                  # Avalonia + Dock shell
│       ├── App.axaml(.cs)
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs
│       │   └── AgentPaneViewModel.cs
│       ├── Views/
│       │   ├── MainWindow.axaml(.cs)
│       │   └── AgentPaneView.axaml(.cs)
│       └── Docking/DockFactory.cs
├── tests/
│   └── Styloagent.Core.Tests/
│       ├── Fixtures/channel/            # a fake agent-channel tree
│       ├── ManifestStoreTests.cs
│       ├── ChannelManifestSeederTests.cs
│       └── AgentSessionTests.cs
└── spikes/                             # THROWAWAY — deleted before Slice 1 merge
    ├── spike-a-pty/  spike-b-term/  spike-c-md/  spike-d-mcp/
```

Rationale: `Styloagent.Core` is UI-free so every engine is unit-testable headless. `IPtySession` lives in Core; the `Porta.Pty` implementation lives in `Styloagent.Terminal` so the renderer/PTY is swappable without touching logic. Files split by responsibility (model, config, seeding, sessions), not by layer.

---

## Phase 0 — Prerequisite: extract `Mostlylucid.LucidView.Markdown`

> Lands in `~/RiderProjects/lucidview/`. Goal: a versioned NuGet carrying lucidVIEW's markdown *presentation* (LiveMarkdown.Avalonia + Naiad mermaid + syntax highlighting + theme/font resources), with app chrome (FluentAvalonia shell, QuestPDF print, StyloExtract) left behind. lucidVIEW then consumes its own package.

### Task 0.1: Create the library project and move the rendering brain

**Files (in `~/RiderProjects/lucidview/`):**
- Create: `src/Mostlylucid.LucidView.Markdown/Mostlylucid.LucidView.Markdown.csproj`
- Create: `src/Mostlylucid.LucidView.Markdown/MarkdownView.axaml(.cs)` (the reusable control + a `Render(string markdown)` entry point)
- Move: the markdown-rendering XAML styles, theme resource dictionaries, and font assets out of `MarkdownViewer/` into the new library
- Reference: `external/LiveMarkdown.Avalonia/src/LiveMarkdown.Avalonia/LiveMarkdown.Avalonia.csproj` and `Naiad/src/Naiad/Naiad.csproj`

- [ ] **Step 1: Scaffold the library project**

```bash
cd ~/RiderProjects/lucidview
dotnet new classlib -n Mostlylucid.LucidView.Markdown -o src/Mostlylucid.LucidView.Markdown -f net10.0
```

Edit the csproj to add Avalonia + the project references (mirror the versions already pinned in `MarkdownViewer/MarkdownViewer.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>Mostlylucid.LucidView.Markdown</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.12" />
    <ProjectReference Include="..\..\external\LiveMarkdown.Avalonia\src\LiveMarkdown.Avalonia\LiveMarkdown.Avalonia.csproj" />
    <ProjectReference Include="..\..\Naiad\src\Naiad\Naiad.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the control with a minimal public surface**

Create `MarkdownView.axaml.cs` exposing a bindable `Markdown` string property that feeds LiveMarkdown's control. Keep it thin — it wraps LiveMarkdown + applies the moved theme resources. (Confirm LiveMarkdown's control type name against `external/LiveMarkdown.Avalonia/src/LiveMarkdown.Avalonia/` — it is the type used in `MarkdownViewer`'s XAML today.)

- [ ] **Step 3: Build the library**

Run: `dotnet build src/Mostlylucid.LucidView.Markdown`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.LucidView.Markdown
git commit -m "feat(markdown): extract LucidView.Markdown presentation library"
```

### Task 0.2: Prove the library renders, standalone

**Files:** Create a throwaway `spikes/md-standalone/` Avalonia app in the lucidview repo that references the library.

- [ ] **Step 1: Scaffold a minimal Avalonia app** referencing `Mostlylucid.LucidView.Markdown`, with a window containing one `MarkdownView`.
- [ ] **Step 2: Feed it a markdown string** containing a heading, a fenced code block, a table, and a `mermaid` fenced block.
- [ ] **Step 3: Run it**

Run: `dotnet run --project spikes/md-standalone`
Expected: window shows styled markdown; the mermaid block renders as a diagram (Naiad), offline.

- [ ] **Step 4: Record the result** in `spikes/md-standalone/RESULT.md` (works / gaps / the exact LiveMarkdown control type + any missing resources). Do NOT commit the throwaway app; commit only `RESULT.md` if useful, else discard.

### Task 0.3: Repoint lucidVIEW at the extracted package (dogfood) and pack

**Files:** Modify `MarkdownViewer/MarkdownViewer.csproj` (swap the direct LiveMarkdown/Naiad wiring for a reference to the new library).

- [ ] **Step 1: Replace** the `LiveMarkdown.Avalonia` + `Naiad` ProjectReferences and moved styles in `MarkdownViewer` with a single `ProjectReference` to `Mostlylucid.LucidView.Markdown`.
- [ ] **Step 2: Build + run lucidVIEW**

Run: `dotnet build MarkdownViewer && dotnet run --project MarkdownViewer`
Expected: lucidVIEW still opens and renders a `.md` file identically (no visual regression on themes/mermaid).

- [ ] **Step 3: Pack the NuGet to a local feed**

```bash
dotnet pack src/Mostlylucid.LucidView.Markdown -c Release -o ~/RiderProjects/local-nuget
```
Expected: `Mostlylucid.LucidView.Markdown.*.nupkg` in `~/RiderProjects/local-nuget`.

- [ ] **Step 4: Commit**

```bash
git add MarkdownViewer/MarkdownViewer.csproj
git commit -m "refactor(markdown): lucidVIEW consumes extracted LucidView.Markdown"
```

**Phase 0 deliverable:** a locally-packed `Mostlylucid.LucidView.Markdown` NuGet; lucidVIEW dogfoods it with no regression.

---

## Phase 1 — Slice 0 spikes (THROWAWAY, gate the stack)

> Each spike is a validation experiment with a pass/fail criterion and a `RESULT.md`. They live under `~/RiderProjects/styloagent/spikes/` and are **deleted before Slice 1 is merged**. Their job is to pin external-library APIs so Slice 1 builds on confirmed knowledge. Record exact type/method names discovered — Slice 1 tasks reference them.

### Task 1.A: PTY stdin injection + focus coexistence

**Success criterion:** a console program spawns an interactive process via `Porta.Pty`, and can write to its stdin programmatically (`WriterStream`) *while* forwarding console keystrokes, reading stdout (`ReaderStream`) throughout.

- [ ] **Step 1: Scaffold** `spikes/spike-a-pty` console app; add `Porta.Pty`.

```bash
cd ~/RiderProjects/styloagent
dotnet new console -o spikes/spike-a-pty
dotnet add spikes/spike-a-pty package Porta.Pty
```

- [ ] **Step 2: Spawn `bash`** with `PtyProvider.SpawnAsync(new PtyOptions { App = "/bin/bash", Cols = 120, Rows = 30 })`; start a reader loop on `connection.ReaderStream` printing decoded output.
- [ ] **Step 3: On a timer, inject** `await connection.WriterStream.WriteAsync(Encoding.UTF8.GetBytes("echo INJECTED_$(date +%s)\n"))` while the user can also type (forward `Console.ReadKey` to the same `WriterStream`).
- [ ] **Step 4: Run and observe**

Run: `dotnet run --project spikes/spike-a-pty`
Expected: `INJECTED_...` lines appear on the timer AND typed commands also run — both reach the shell without corrupting each other.

- [ ] **Step 5: Record** exact `Porta.Pty` types/members used (`PtyProvider`, `PtyOptions` fields, `IPtyConnection.WriterStream/ReaderStream/Resize/ProcessExited`) in `spikes/spike-a-pty/RESULT.md`. **These names feed Task 2.4/2.5.**

### Task 1.B: XTerm.NET render on Avalonia 11.3

**Success criterion:** an Avalonia 11.3 app renders live PTY output through XTerm.NET (colour + resize + scrollback) on macOS.

- [ ] **Step 1: Scaffold** `spikes/spike-b-term` Avalonia 11.3 app; add `XTerm.NET` (headless engine) and either `Iciclecreek.Avalonia.Terminal` (try the 11.x-compatible release first) or plan a custom control.
- [ ] **Step 2: Wire** Spike A's PTY into the terminal control/engine: feed `ReaderStream` bytes to the XTerm.NET parser, render its screen buffer, route key input to `WriterStream`.
- [ ] **Step 3: Run** `dotnet run --project spikes/spike-b-term`; run `top` and `ls --color` inside.
Expected: full-screen TUI + colour render correctly; window resize propagates a PTY `Resize(cols,rows)`.
- [ ] **Step 4: Record** in `RESULT.md`: did Iciclecreek 11.x work, or is a custom XTerm.NET→Avalonia control needed? Capture the exact control/engine API. **Feeds Task 2.5.**

### Task 1.C: Extracted markdown package renders a channel message

**Success criterion:** the Phase-0 `Mostlylucid.LucidView.Markdown` package, consumed from a fresh Avalonia 11.3 app, renders a real channel reply file (with a mermaid block) offline.

- [ ] **Step 1: Scaffold** `spikes/spike-c-md`; add the local package (from `~/RiderProjects/local-nuget`, via a `nuget.config` local source).
- [ ] **Step 2: Load** a copy of a real reply (e.g. the reply shape: Direct answer / Why / Relevant docs / Relevant code) plus a `mermaid` graph block; bind it to `MarkdownView.Markdown`.
- [ ] **Step 3: Run**; Expected: renders styled, mermaid included, no network.
- [ ] **Step 4: Record** the package consumption steps (nuget.config local source) in `RESULT.md`. **Feeds Slice 2, not Slice 1.**

### Task 1.D: MCP config injection into a spawned `claude`

**Success criterion:** launching `claude` with a Styloagent-provided MCP config makes a trivial `styloagent` MCP tool callable, WITHOUT writing config into the worktree repo.

- [ ] **Step 1: Build a trivial MCP server** (`spikes/spike-d-mcp/server`) using the `ModelContextProtocol` C# SDK exposing one tool `ping` → returns `"pong"`.
- [ ] **Step 2: Launch `claude`** from the spike pointing at that server via a session-scoped config. Try, in order: `claude --mcp-config <abs-path-to.json>`; then a `.mcp.json` placed in a scratch cwd (NOT a real repo); then `claude mcp add`. Record which injects per-session cleanly.
- [ ] **Step 3: In the `claude` session, call** the tool (`mcp__styloagent__ping` or the SDK's resolved name); Expected: `pong`.
- [ ] **Step 4: Record** the exact working injection mechanism + tool-name namespacing in `RESULT.md`. **Feeds Slice 2 (risk #6).**

**Phase 1 deliverable:** four `RESULT.md` files pinning the PTY, terminal-render, markdown-package, and MCP-injection APIs. Gate: all four "works" (or a documented fallback chosen) before Slice 1.

---

## Phase 2 — Slice 1: walking skeleton (TDD, real software)

> Ships an Avalonia app that seeds a fleet manifest from the channel, spawns ONE owned agent terminal, and dehydrates/rehydrates it. All Core logic is TDD'd headless; terminal/shell integration builds on Phase-1 results.

### Task 2.1: Solution + Core model + VYaml round-trip

**Files:**
- Create: `Styloagent.sln`, `src/Styloagent.Core/Styloagent.Core.csproj`, `tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj`
- Create: `src/Styloagent.Core/Model/SessionState.cs`, `Model/AgentTransport.cs`, `Model/AgentManifestEntry.cs`
- Create: `src/Styloagent.Core/Config/ManifestStore.cs`
- Test: `tests/Styloagent.Core.Tests/ManifestStoreTests.cs`

**Interfaces:**
- Produces: `enum SessionState { Unspawned, Live, Dehydrated }`; `enum TransportKind { Local, Ssh }`; `record AgentTransport(TransportKind Kind, string? SshHost, string? CredentialRef)`; `record AgentManifestEntry(string Prefix, string Repo, string Worktree, string LaunchPromptPath, string RestartPromptPath, string SavedContextPath, AgentTransport Transport)`; `class ManifestStore` with `Task SaveAsync(string path, IReadOnlyList<AgentManifestEntry>)` and `Task<IReadOnlyList<AgentManifestEntry>> LoadAsync(string path)`.

- [ ] **Step 1: Scaffold solution and projects**

```bash
cd ~/RiderProjects/styloagent
dotnet new sln -n Styloagent
dotnet new classlib -n Styloagent.Core -o src/Styloagent.Core -f net10.0
dotnet new xunit -n Styloagent.Core.Tests -o tests/Styloagent.Core.Tests -f net10.0
dotnet sln add src/Styloagent.Core tests/Styloagent.Core.Tests
dotnet add tests/Styloagent.Core.Tests reference src/Styloagent.Core
dotnet add src/Styloagent.Core package VYaml
```

- [ ] **Step 2: Write the model types**

`Model/SessionState.cs`:
```csharp
namespace Styloagent.Core.Model;

public enum SessionState { Unspawned, Live, Dehydrated }
```

`Model/AgentTransport.cs`:
```csharp
namespace Styloagent.Core.Model;

public enum TransportKind { Local, Ssh }

public sealed record AgentTransport(TransportKind Kind, string? SshHost = null, string? CredentialRef = null)
{
    public static readonly AgentTransport Local = new(TransportKind.Local);
}
```

`Model/AgentManifestEntry.cs`:
```csharp
namespace Styloagent.Core.Model;

public sealed record AgentManifestEntry(
    string Prefix,
    string Repo,
    string Worktree,
    string LaunchPromptPath,
    string RestartPromptPath,
    string SavedContextPath,
    AgentTransport Transport);
```

- [ ] **Step 3: Write the failing test**

`ManifestStoreTests.cs`:
```csharp
using Styloagent.Core.Config;
using Styloagent.Core.Model;
using Xunit;

public class ManifestStoreTests
{
    [Fact]
    public async Task Save_then_Load_roundtrips_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.yaml");
        var entries = new List<AgentManifestEntry>
        {
            new("foss-", "/repo", "/repo/wt-foss", "/ch/launch-prompts/foss.md",
                "/ch/launch-prompts/foss-restart.md", "/ch/saved-context/foss-context.md",
                AgentTransport.Local),
        };
        var store = new ManifestStore();

        await store.SaveAsync(path, entries);
        var loaded = await store.LoadAsync(path);

        Assert.Single(loaded);
        Assert.Equal("foss-", loaded[0].Prefix);
        Assert.Equal(TransportKind.Local, loaded[0].Transport.Kind);
    }
}
```

- [ ] **Step 4: Run test, verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter ManifestStoreTests`
Expected: FAIL — `ManifestStore` does not exist.

- [ ] **Step 5: Implement `ManifestStore` with VYaml**

`Config/ManifestStore.cs` (VYaml uses source-gen; annotate the DTOs with `[YamlObject]` on `partial` types. Confirm the exact `VYaml.Serialization.YamlSerializer` API against the VYaml README from Spike-free docs — it is `YamlSerializer.Serialize<T>` / `Deserialize<T>`):
```csharp
using System.Text;
using Styloagent.Core.Model;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Config;

[YamlObject] public partial class ManifestFile { public List<ManifestRow> Agents { get; set; } = new(); }

[YamlObject]
public partial class ManifestRow
{
    public string Prefix { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Worktree { get; set; } = "";
    public string LaunchPrompt { get; set; } = "";
    public string RestartPrompt { get; set; } = "";
    public string SavedContext { get; set; } = "";
    public string Transport { get; set; } = "local";
    public string? SshHost { get; set; }
    public string? CredentialRef { get; set; }
}

public sealed class ManifestStore
{
    public async Task SaveAsync(string path, IReadOnlyList<AgentManifestEntry> entries)
    {
        var file = new ManifestFile
        {
            Agents = entries.Select(e => new ManifestRow
            {
                Prefix = e.Prefix, Repo = e.Repo, Worktree = e.Worktree,
                LaunchPrompt = e.LaunchPromptPath, RestartPrompt = e.RestartPromptPath,
                SavedContext = e.SavedContextPath,
                Transport = e.Transport.Kind == TransportKind.Ssh ? "ssh" : "local",
                SshHost = e.Transport.SshHost, CredentialRef = e.Transport.CredentialRef,
            }).ToList(),
        };
        var bytes = YamlSerializer.Serialize(file);
        await File.WriteAllBytesAsync(path, bytes.ToArray());
    }

    public async Task<IReadOnlyList<AgentManifestEntry>> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var file = YamlSerializer.Deserialize<ManifestFile>(bytes);
        return file.Agents.Select(r => new AgentManifestEntry(
            r.Prefix, r.Repo, r.Worktree, r.LaunchPrompt, r.RestartPrompt, r.SavedContext,
            r.Transport == "ssh" ? new AgentTransport(TransportKind.Ssh, r.SshHost, r.CredentialRef)
                                 : AgentTransport.Local)).ToList();
    }
}
```

- [ ] **Step 6: Run test, verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter ManifestStoreTests`
Expected: PASS. (If VYaml requires a source-gen `[YamlObject]` partial-class registration, the failure message will name it — add the annotation and re-run.)

- [ ] **Step 7: Commit**

```bash
git add Styloagent.sln src/Styloagent.Core tests/Styloagent.Core.Tests
git commit -m "feat(core): fleet manifest model + VYaml store"
```

### Task 2.2: Seed the manifest from a channel directory

**Files:**
- Create: `src/Styloagent.Core/Seeding/ChannelManifestSeeder.cs`
- Test: `tests/Styloagent.Core.Tests/ChannelManifestSeederTests.cs`
- Create fixtures: `tests/Styloagent.Core.Tests/Fixtures/channel/{launch-prompts,saved-context}/...`

**Interfaces:**
- Consumes: `AgentManifestEntry` (Task 2.1).
- Produces: `class ChannelManifestSeeder` with `Task<IReadOnlyList<AgentManifestEntry>> SeedAsync(string channelRoot, IReadOnlyDictionary<string,string> prefixToWorktree)`. Prefixes are discovered from `saved-context/<prefix>-context.md` filenames; the worktree map is supplied by the caller (the PROTOCOL routing table is prose — human-confirmed, per spec risk #5).

- [ ] **Step 1: Build the fixture channel tree**

```bash
cd ~/RiderProjects/styloagent/tests/Styloagent.Core.Tests
mkdir -p Fixtures/channel/saved-context Fixtures/channel/launch-prompts
printf '# foss context\n' > Fixtures/channel/saved-context/foss-context.md
printf '# overview context\n' > Fixtures/channel/saved-context/overview-context.md
printf '# restart\n' > Fixtures/channel/launch-prompts/foss-restart.md
```

Mark the fixtures to copy to output — add to the test csproj:
```xml
<ItemGroup>
  <None Include="Fixtures/**/*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing test**

```csharp
using Styloagent.Core.Seeding;
using Xunit;

public class ChannelManifestSeederTests
{
    [Fact]
    public async Task Seeds_one_entry_per_saved_context_file()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "channel");
        var map = new Dictionary<string, string> { ["foss-"] = "/repo/wt-foss" };
        var seeder = new ChannelManifestSeeder();

        var entries = await seeder.SeedAsync(root, map);

        var foss = Assert.Single(entries, e => e.Prefix == "foss-");
        Assert.Equal("/repo/wt-foss", foss.Worktree);
        Assert.EndsWith("foss-context.md", foss.SavedContextPath);
        Assert.EndsWith("foss-restart.md", foss.RestartPromptPath);
    }

    [Fact]
    public async Task Unmapped_prefix_still_seeds_with_empty_worktree()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "channel");
        var seeder = new ChannelManifestSeeder();

        var entries = await seeder.SeedAsync(root, new Dictionary<string, string>());

        Assert.Contains(entries, e => e.Prefix == "overview-" && e.Worktree == "");
    }
}
```

- [ ] **Step 3: Run test, verify it fails**

Run: `dotnet test --filter ChannelManifestSeederTests`
Expected: FAIL — `ChannelManifestSeeder` not defined.

- [ ] **Step 4: Implement the seeder**

```csharp
using Styloagent.Core.Model;

namespace Styloagent.Core.Seeding;

public sealed class ChannelManifestSeeder
{
    public Task<IReadOnlyList<AgentManifestEntry>> SeedAsync(
        string channelRoot, IReadOnlyDictionary<string, string> prefixToWorktree)
    {
        var savedContextDir = Path.Combine(channelRoot, "saved-context");
        var launchDir = Path.Combine(channelRoot, "launch-prompts");
        var entries = new List<AgentManifestEntry>();

        if (!Directory.Exists(savedContextDir))
            return Task.FromResult<IReadOnlyList<AgentManifestEntry>>(entries);

        foreach (var file in Directory.EnumerateFiles(savedContextDir, "*-context.md").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);                 // "foss-context.md"
            var prefix = name[..^"context.md".Length];          // "foss-"
            var restart = Path.Combine(launchDir, $"{prefix}restart.md");
            entries.Add(new AgentManifestEntry(
                Prefix: prefix,
                Repo: "",
                Worktree: prefixToWorktree.TryGetValue(prefix, out var wt) ? wt : "",
                LaunchPromptPath: File.Exists(restart) ? restart : "",
                RestartPromptPath: File.Exists(restart) ? restart : "",
                SavedContextPath: file,
                Transport: AgentTransport.Local));
        }
        return Task.FromResult<IReadOnlyList<AgentManifestEntry>>(entries);
    }
}
```

- [ ] **Step 5: Run tests, verify they pass**

Run: `dotnet test --filter ChannelManifestSeederTests`
Expected: PASS (both facts).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Core/Seeding tests/Styloagent.Core.Tests
git commit -m "feat(core): seed fleet manifest from channel saved-context"
```

### Task 2.3: PTY abstraction + `AgentSession` state machine (fake PTY)

**Files:**
- Create: `src/Styloagent.Core/Sessions/IPtySession.cs`, `IPtyLauncher.cs`, `PtySpawnOptions.cs`, `AgentSession.cs`
- Create: `src/Styloagent.Core/Abstractions/IFileWatcher.cs`
- Test: `tests/Styloagent.Core.Tests/AgentSessionTests.cs`

**Interfaces:**
- Produces:
  - `interface IPtySession : IAsyncDisposable { ValueTask WriteAsync(string text, CancellationToken ct = default); event Action<string>? Output; event Action? Exited; bool IsIdle { get; } void Resize(int cols, int rows); }`
  - `record PtySpawnOptions(string Command, IReadOnlyList<string> Args, string WorkingDirectory, IReadOnlyDictionary<string,string>? Env, int Cols, int Rows)`
  - `interface IPtyLauncher { Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default); }`
  - `interface IFileWatcher { Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default); }`
  - `class AgentSession` — ctor `(AgentManifestEntry manifest, IPtyLauncher launcher, IFileWatcher watcher)`; `SessionState State`; `Task SpawnAsync(string launchPrompt, CancellationToken ct = default)`; `Task<bool> DehydrateAsync(TimeSpan ackTimeout, CancellationToken ct = default)`; `Task RehydrateAsync(string restartPrompt, CancellationToken ct = default)`; `event Action<string>? Output`.
- Consumes: `AgentManifestEntry` (2.1).

Behaviour locked by tests: Spawn (Unspawned→Live) launches a PTY in the worktree and writes the launch prompt. Dehydrate (Live→Dehydrated) writes a "checkpoint" instruction, waits for the saved-context file to change; if it changes within `ackTimeout` → dispose PTY, state Dehydrated, return true; if it does NOT change → **do not dispose**, state stays Live, return false (spec: never lose context). Rehydrate (Dehydrated→Live) launches a new PTY and writes the restart prompt.

- [ ] **Step 1: Write the interfaces** (exactly the signatures in the Interfaces block above) in their files.

- [ ] **Step 2: Write the failing tests with a fake PTY + fake watcher**

```csharp
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

public class AgentSessionTests
{
    private sealed class FakePty : IPtySession
    {
        public List<string> Writes { get; } = new();
        public bool Disposed { get; private set; }
        public event Action<string>? Output;
        public event Action? Exited;
        public bool IsIdle => true;
        public void Resize(int cols, int rows) { }
        public ValueTask WriteAsync(string text, CancellationToken ct = default) { Writes.Add(text); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void EmitExit() => Exited?.Invoke();
    }

    private sealed class FakeLauncher : IPtyLauncher
    {
        public List<FakePty> Spawned { get; } = new();
        public PtySpawnOptions? Last { get; private set; }
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
        {
            Last = o; var p = new FakePty(); Spawned.Add(p); return Task.FromResult<IPtySession>(p);
        }
    }

    private sealed class FakeWatcher : IFileWatcher
    {
        public bool WillChange = true;
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(WillChange);
    }

    private static AgentManifestEntry Entry() => new(
        "foss-", "/repo", "/repo/wt-foss", "/ch/lp/foss.md", "/ch/lp/foss-restart.md",
        "/ch/sc/foss-context.md", AgentTransport.Local);

    [Fact]
    public async Task Spawn_launches_in_worktree_and_sends_prompt()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        await s.SpawnAsync("LAUNCH PROMPT");

        Assert.Equal(SessionState.Live, s.State);
        Assert.Equal("/repo/wt-foss", launcher.Last!.WorkingDirectory);
        Assert.Contains(launcher.Spawned[0].Writes, w => w.Contains("LAUNCH PROMPT"));
    }

    [Fact]
    public async Task Dehydrate_with_ack_disposes_pty_and_sets_state()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");

        var ok = await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        Assert.True(ok);
        Assert.Equal(SessionState.Dehydrated, s.State);
        Assert.True(launcher.Spawned[0].Disposed);
    }

    [Fact]
    public async Task Dehydrate_without_ack_keeps_session_live()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = false });
        await s.SpawnAsync("LP");

        var ok = await s.DehydrateAsync(TimeSpan.FromMilliseconds(50));

        Assert.False(ok);
        Assert.Equal(SessionState.Live, s.State);
        Assert.False(launcher.Spawned[0].Disposed);   // never lose context
    }

    [Fact]
    public async Task Rehydrate_spawns_new_pty_and_sends_restart()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");
        await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        await s.RehydrateAsync("RESTART PROMPT");

        Assert.Equal(SessionState.Live, s.State);
        Assert.Equal(2, launcher.Spawned.Count);
        Assert.Contains(launcher.Spawned[1].Writes, w => w.Contains("RESTART PROMPT"));
    }
}
```

- [ ] **Step 3: Run tests, verify they fail**

Run: `dotnet test --filter AgentSessionTests`
Expected: FAIL — `AgentSession` not defined.

- [ ] **Step 4: Implement `AgentSession`**

```csharp
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

public sealed class AgentSession
{
    private readonly AgentManifestEntry _manifest;
    private readonly IPtyLauncher _launcher;
    private readonly IFileWatcher _watcher;
    private IPtySession? _pty;

    public AgentSession(AgentManifestEntry manifest, IPtyLauncher launcher, IFileWatcher watcher)
        => (_manifest, _launcher, _watcher) = (manifest, launcher, watcher);

    public SessionState State { get; private set; } = SessionState.Unspawned;
    public event Action<string>? Output;

    public async Task SpawnAsync(string launchPrompt, CancellationToken ct = default)
    {
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            Command: "claude", Args: Array.Empty<string>(),
            WorkingDirectory: _manifest.Worktree, Env: null, Cols: 120, Rows: 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(launchPrompt + "\n", ct);
        State = SessionState.Live;
    }

    public async Task<bool> DehydrateAsync(TimeSpan ackTimeout, CancellationToken ct = default)
    {
        if (_pty is null || State != SessionState.Live) return false;
        await _pty.WriteAsync(
            $"Please checkpoint your context to {_manifest.SavedContextPath}, then stand by.\n", ct);
        var acked = await _watcher.WaitForChangeAsync(_manifest.SavedContextPath, ackTimeout, ct);
        if (!acked) return false;                 // never lose context — keep it live
        _pty.Output -= OnOutput;
        await _pty.DisposeAsync();
        _pty = null;
        State = SessionState.Dehydrated;
        return true;
    }

    public async Task RehydrateAsync(string restartPrompt, CancellationToken ct = default)
    {
        if (State != SessionState.Dehydrated) return;
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            "claude", Array.Empty<string>(), _manifest.Worktree, null, 120, 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(restartPrompt + "\n", ct);
        State = SessionState.Live;
    }

    private void OnOutput(string chunk) => Output?.Invoke(chunk);
}
```

- [ ] **Step 5: Run tests, verify they pass**

Run: `dotnet test --filter AgentSessionTests`
Expected: PASS (4 facts).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Core/Sessions src/Styloagent.Core/Abstractions tests/Styloagent.Core.Tests
git commit -m "feat(core): AgentSession spawn/dehydrate/rehydrate state machine"
```

### Task 2.4: Real `Porta.Pty` implementation + integration test

**Files:**
- Create: `src/Styloagent.Terminal/Styloagent.Terminal.csproj`, `PortaPtySession.cs`, `PortaPtyLauncher.cs`
- Create: `src/Styloagent.Core/Sessions/FileSystemFileWatcher.cs` (real `IFileWatcher`)
- Test: `tests/Styloagent.Core.Tests/PortaPtyIntegrationTests.cs`

**Interfaces:**
- Consumes: `IPtySession`, `IPtyLauncher`, `PtySpawnOptions` (2.3), and Spike-A's confirmed `Porta.Pty` API.
- Produces: `PortaPtyLauncher : IPtyLauncher`; `FileSystemFileWatcher : IFileWatcher`.

- [ ] **Step 1: Scaffold the Terminal project**

```bash
dotnet new classlib -n Styloagent.Terminal -o src/Styloagent.Terminal -f net10.0
dotnet sln add src/Styloagent.Terminal
dotnet add src/Styloagent.Terminal reference src/Styloagent.Core
dotnet add src/Styloagent.Terminal package Porta.Pty
dotnet add tests/Styloagent.Core.Tests reference src/Styloagent.Terminal
```

- [ ] **Step 2: Implement `PortaPtySession` + `PortaPtyLauncher`** using the exact `Porta.Pty` types recorded in `spikes/spike-a-pty/RESULT.md` (`PtyProvider.SpawnAsync(PtyOptions)`, `IPtyConnection.WriterStream/ReaderStream/Resize/ProcessExited`). `PortaPtySession` wraps `IPtyConnection`: `WriteAsync` → `WriterStream.WriteAsync(UTF8 bytes)`; a background loop reads `ReaderStream` → decodes → raises `Output`; `IsIdle` heuristic = no output for N ms (start simple: time-since-last-output threshold); `DisposeAsync` closes the connection.

- [ ] **Step 3: Implement `FileSystemFileWatcher`** — `WaitForChangeAsync` uses a `FileSystemWatcher` on the file's directory filtered to its name (plus a last-write-time poll fallback), completing `true` on change or `false` on timeout.

- [ ] **Step 4: Write the integration test (real PTY, `bash`)**

```csharp
using Styloagent.Core.Sessions;
using Styloagent.Terminal;
using Xunit;

public class PortaPtyIntegrationTests
{
    [Fact(Timeout = 15000)]
    [Trait("Category", "Integration")]
    public async Task Spawns_bash_writes_and_reads_output()
    {
        var launcher = new PortaPtyLauncher();
        var got = new TaskCompletionSource<string>();
        var opts = new PtySpawnOptions("/bin/bash", new[] { "--norc" },
            Environment.CurrentDirectory, null, 80, 24);

        await using var pty = await launcher.SpawnAsync(opts);
        pty.Output += chunk => { if (chunk.Contains("HELLO_PTY")) got.TrySetResult(chunk); };
        await pty.WriteAsync("echo HELLO_PTY\n");

        var completed = await Task.WhenAny(got.Task, Task.Delay(10000));
        Assert.Same(got.Task, completed);
    }
}
```

- [ ] **Step 5: Run the integration test**

Run: `dotnet test --filter PortaPtyIntegrationTests`
Expected: PASS on macOS (spawns real `bash`, sees `HELLO_PTY`).

- [ ] **Step 6: Commit**

```bash
git add src/Styloagent.Terminal src/Styloagent.Core/Sessions/FileSystemFileWatcher.cs tests
git commit -m "feat(terminal): Porta.Pty session + launcher + file-change watcher"
```

### Task 2.5: Terminal render control (Avalonia + XTerm.NET)

**Files:**
- Create: `src/Styloagent.Terminal/TerminalControl.axaml(.cs)`

**Interfaces:**
- Consumes: `IPtySession` (2.3); Spike-B's confirmed render approach.
- Produces: `class TerminalControl : Avalonia control` with `void Attach(IPtySession session)` — feeds `session.Output` into the XTerm.NET buffer, renders it, and routes key input to `session.WriteAsync`; forwards size changes to `session.Resize`.

- [ ] **Step 1: Add Avalonia + the render dependency** to `Styloagent.Terminal.csproj` per Spike B's result (Iciclecreek 11.x control, or `XTerm.NET` + a custom render surface).
- [ ] **Step 2: Implement `TerminalControl.Attach(IPtySession)`** exactly as validated in Spike B — the spike already proved the wiring; port it into a reusable control bound to our `IPtySession`.
- [ ] **Step 3: Manual smoke** — a temporary window that `Attach`es a `PortaPtySession` running `bash`.
Run: `dotnet run` (temp harness); Expected: interactive shell renders + accepts input; resize propagates.
- [ ] **Step 4: Commit**

```bash
git add src/Styloagent.Terminal/TerminalControl.axaml src/Styloagent.Terminal/TerminalControl.axaml.cs
git commit -m "feat(terminal): XTerm.NET-backed Avalonia TerminalControl over IPtySession"
```

### Task 2.6: Dock shell hosting one agent pane + lifecycle buttons

**Files:**
- Create: `src/Styloagent.App/Styloagent.App.csproj`, `App.axaml(.cs)`, `Program.cs`
- Create: `Views/MainWindow.axaml(.cs)`, `Views/AgentPaneView.axaml(.cs)`
- Create: `ViewModels/MainWindowViewModel.cs`, `ViewModels/AgentPaneViewModel.cs`
- Create: `Docking/DockFactory.cs`
- Create: `Config/PresentationStore.cs` (VYaml sidecar for colour + display name + layout)

**Interfaces:**
- Consumes: `AgentSession` (2.3), `TerminalControl` (2.5), `ManifestStore`/`ChannelManifestSeeder` (2.1/2.2), `PortaPtyLauncher`/`FileSystemFileWatcher` (2.4).
- Produces: `AgentPaneViewModel` with `string DisplayName`, `string BorderColorHex`, `SessionState State`, and commands `SpawnCommand`, `DehydrateCommand`, `RehydrateCommand`, `RenameCommand`; `MainWindowViewModel` that loads/seeds the manifest and creates one `AgentPaneViewModel`.

- [ ] **Step 1: Scaffold the Avalonia app + Dock**

```bash
dotnet new install Avalonia.Templates
dotnet new avalonia.mvvm -n Styloagent.App -o src/Styloagent.App
dotnet sln add src/Styloagent.App
dotnet add src/Styloagent.App reference src/Styloagent.Core src/Styloagent.Terminal
dotnet add src/Styloagent.App package Dock.Avalonia --version 11.*
dotnet add src/Styloagent.App package Dock.Model.Mvvm --version 11.*
```

- [ ] **Step 2: `PresentationStore`** — VYaml load/save of `record AgentPresentation(string Prefix, string DisplayName, string BorderColorHex)` list (same `[YamlObject]` pattern as Task 2.1). Default colour derived deterministically from the prefix (stable per agent).
- [ ] **Step 3: `AgentPaneViewModel`** — wraps one `AgentSession`; exposes `DisplayName`, `BorderColorHex`, `State`; `SpawnCommand` reads the launch prompt from `manifest.LaunchPromptPath` (falling back to a minimal built-in brief if empty) and calls `AgentSession.SpawnAsync`; `DehydrateCommand`/`RehydrateCommand` call the session; `RenameCommand` updates `DisplayName` and persists via `PresentationStore`. The view binds a `TerminalControl` and calls `Attach` when the session goes Live (subscribe to a `SessionLive` event the VM raises).
- [ ] **Step 4: `DockFactory`** — a `Dock.Model.Mvvm.Factory` that builds a `DocumentDock` containing one document whose content is the `AgentPaneView`. Border brush bound to `BorderColorHex`; tab title bound to `DisplayName`.
- [ ] **Step 5: `MainWindowViewModel`** — on startup: resolve the channel root (config or default `/tmp/agent-channel`), `ChannelManifestSeeder.SeedAsync` (with a human-confirmed prefix→worktree map loaded from the manifest YAML if present), pick the first entry, build one `AgentPaneViewModel`. Persist/restore Dock layout + presentation via the sidecar (layout serialize/restore is Dock's; **do not serialize PTY state**).
- [ ] **Step 6: Manual end-to-end verification**

Run: `dotnet run --project src/Styloagent.App`
Expected, in order:
  1. App opens with one docked tab named after a real prefix, in its colour.
  2. **Spawn** → a `claude` (or a stand-in shell if `claude` unavailable) launches in the worktree; terminal renders; the launch prompt is sent.
  3. **Rename** the tab → title updates and persists across restart.
  4. **Dehydrate** → the agent is asked to checkpoint; on the saved-context file changing, the PTY closes and the tab dims to a ghost.
  5. **Rehydrate** → a new PTY spawns and the restart prompt is sent; tab goes live.
  6. Close + reopen the app → the layout, tab name, and colour restore (as an unspawned/ghost slot — no live PTY resurrected).

- [ ] **Step 7: Commit**

```bash
git add src/Styloagent.App
git commit -m "feat(app): Dock shell hosting one owned agent terminal + lifecycle"
```

### Task 2.7: Clean up spikes + document the walking skeleton

- [ ] **Step 1: Delete** the throwaway `spikes/` directory (their knowledge is captured in code + `RESULT.md` summaries folded into the plan/spec).

```bash
git rm -r spikes
```

- [ ] **Step 2: Add a short `README.md`** to the repo: what Styloagent is (one paragraph + link to the spec), how to build/run (`dotnet run --project src/Styloagent.App`), and the Slice-1 scope (one owned agent). 
- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: walking-skeleton README; remove spikes"
```

**Slice 1 deliverable:** an Avalonia cockpit that seeds a manifest from the channel, spawns one owned agent terminal in a dockable tab, renames + colours it, and dehydrates/rehydrates it with observed-ack safety — on a stack validated end-to-end.

---

## Self-Review

**Spec coverage (this plan's scope = extraction prereq + Slice 0 + Slice 1):**
- Extraction prerequisite (§6.1) → Phase 0 (Tasks 0.1–0.3). ✓
- Slice 0 spikes A–D (§7) → Phase 1 (Tasks 1.A–1.D). ✓
- Slice 1 walking skeleton (§7): manifest seeded from channel → 2.2; spawn one agent (cd→claude→launch-prompt) → 2.3/2.6; rename + border colour → 2.6; layout persists → 2.6; dehydrate (ack→kill) / rehydrate → 2.3/2.6. ✓
- Hard invariant / degrade-never-destroy (§2, §9): dehydrate-without-ack keeps session live (2.3 test); no PTY state serialized (2.6). ✓
- Config = VYaml (§6) → 2.1, 2.6. ✓
- Own the Porta.Pty layer, swappable renderer (§6): `IPtySession` in Core, `Porta.Pty` impl in Terminal (2.3/2.4). ✓
- **Deferred to later plans (correctly out of this plan's scope):** channel projection engine + bus viewer + MCP host + `styloagent` skill + System Map (Slice 2); worktree viewer (Slice 3); broker polish (Slice 4); SSH control router (Slice 5); P4 ingest. Noted, not gaps.

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to". External-library call sites (Porta.Pty exact members, XTerm.NET control, VYaml serializer surface, Dock factory, Claude Code MCP-config flag) are grounded in the Slice-0 spikes that exist to confirm them; each such task cites the `RESULT.md` that pins the API. This is deliberate sequencing, not a placeholder.

**Type consistency:** `IPtySession`/`IPtyLauncher`/`PtySpawnOptions`/`IFileWatcher`/`AgentSession` signatures defined in 2.3 are consumed verbatim in 2.4/2.5/2.6. `AgentManifestEntry` fields from 2.1 are used consistently in 2.2/2.3. `SessionState` values (`Unspawned/Live/Dehydrated`) are consistent throughout.
