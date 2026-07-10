# Orchestration Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Point Styloagent at a project folder from a Welcome screen; scaffold `.styloagent/` (system prompt + PROTOCOL + channel) if missing; launch a single `overview-` agent with the system prompt; watch `.styloagent/proposed-agents.yaml` and show a **PROPOSED** section atop the roster where the human spawns the overview's suggested subsystems.

**Architecture:** Core gains a pure project model (`ProjectConfig`, `ProjectScaffolder`, `ProposedAgentsReader`) with bundled default templates. App gains a Welcome screen (folder pick + recents), a `ProposedTeamViewModel` (file-watch → cards), and `MainWindowViewModel` changes to launch the overview and spawn proposals via the existing add-agent path.

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, VYaml, xUnit, `Mostlylucid.Avalonia.UITesting`.

## Global Constraints

- Project state lives in `<root>/.styloagent/`: `system-prompt.md`, `PROTOCOL.md`, `proposed-agents.yaml`, `channel/{inbox,outbox,archive/inbox,archive/outbox}`, `launch-prompts/`.
- `ProjectScaffolder.Ensure` is **idempotent** — it creates missing dirs/files and writes the DEFAULT system prompt + PROTOCOL only when absent; it NEVER overwrites the project's own files.
- The bootstrap launches **exactly one** agent — `overview-` (cwd = project root, system prompt injected via `--append-system-prompt`) — and **bypasses worktree auto-seeding**.
- `proposed-agents.yaml` schema: `agents:` list of `{prefix, responsibility, dir, launchPrompt}`.
- Proposed-team UI is a **PROPOSED section at the top of the Agents roster**; Spawn / Spawn all reuse the existing add-agent flow.
- Recent projects persist via VYaml in the app config dir (mirror `PresentationStore`/`ManifestStore`).
- **Analyzer rules (inherited `RiderProjects/.editorconfig` treats many CA rules as errors):** no `.First()/.Count()` on `IReadOnlyList`/arrays (use `list[0]`/`list.Count` — CA1826); hoist constant array args to `static readonly` fields (CA1861); stateless instance methods must be `static` or carry a documented `#pragma warning disable CA1822`. Run `dotnet build` and fix every `error CA####`.
- Commit with `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "…"` ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work on `main` (do NOT create branches).

## File Structure

- **Create (Core):** `Projects/ProjectConfig.cs`, `Projects/DefaultTemplates.cs`, `Projects/ProjectScaffolder.cs`, `Projects/ProposedAgent.cs`, `Projects/ProposedAgentsReader.cs`.
- **Create (App):** `Config/RecentProjectsStore.cs`, `Services/IFolderPicker.cs` (+ `Services/StorageFolderPicker.cs`), `ViewModels/WelcomeViewModel.cs`, `ViewModels/ProposedTeamViewModel.cs`, `Views/WelcomeView.axaml(.cs)`.
- **Modify (App):** `ViewModels/MainWindowViewModel.cs`, `Views/AgentsView.axaml`, `App.axaml.cs`.

Reference types (exist — do not redefine): `AgentManifestEntry(string Prefix, string Repo, string Worktree, string LaunchPromptPath, string RestartPromptPath, string SavedContextPath, AgentTransport Transport)`; `enum AgentTransport { Local, … }`; `PresentationStore.DefaultColorFor(string) : string`; `AgentSession(AgentManifestEntry, IPtyLauncher, IFileWatcher, IReadOnlyList<string>? launchArgs = null)`.

---

### Task 1: Project config + scaffolder + default templates (Core)

**Files:**
- Create: `src/Styloagent.Core/Projects/ProjectConfig.cs`, `src/Styloagent.Core/Projects/DefaultTemplates.cs`, `src/Styloagent.Core/Projects/ProjectScaffolder.cs`
- Test: `tests/Styloagent.Core.Tests/ProjectScaffolderTests.cs`

**Interfaces — Produces:**
- `sealed record ProjectConfig(string Root, string ConfigDir, string SystemPromptPath, string ProtocolPath, string ChannelRoot, string ProposedAgentsPath, string LaunchPromptsDir)` with `static ProjectConfig For(string root)`.
- `static class ProjectScaffolder { static ProjectConfig Ensure(string root); }`
- `static class DefaultTemplates { const string SystemPrompt; const string Protocol; }`

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/ProjectScaffolderTests.cs`:

```csharp
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class ProjectScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Ensure_creates_config_tree_and_default_files()
    {
        Directory.CreateDirectory(_root);
        var cfg = ProjectScaffolder.Ensure(_root);

        Assert.True(File.Exists(cfg.SystemPromptPath));
        Assert.True(File.Exists(cfg.ProtocolPath));
        Assert.True(Directory.Exists(Path.Combine(cfg.ChannelRoot, "inbox")));
        Assert.True(Directory.Exists(Path.Combine(cfg.ChannelRoot, "archive", "outbox")));
        Assert.True(Directory.Exists(cfg.LaunchPromptsDir));
        Assert.Equal(Path.Combine(_root, ".styloagent"), cfg.ConfigDir);
        Assert.Contains("proposed-agents.yaml", cfg.ProposedAgentsPath);
    }

    [Fact]
    public void Ensure_is_idempotent_and_never_overwrites_edited_files()
    {
        Directory.CreateDirectory(_root);
        var cfg = ProjectScaffolder.Ensure(_root);
        File.WriteAllText(cfg.SystemPromptPath, "MY EDITED PROMPT");

        var cfg2 = ProjectScaffolder.Ensure(_root); // second run

        Assert.Equal("MY EDITED PROMPT", File.ReadAllText(cfg2.SystemPromptPath));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "ProjectScaffolderTests" --nologo`
Expected: FAIL — `ProjectScaffolder`/`ProjectConfig` don't exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Styloagent.Core/Projects/ProjectConfig.cs`:

```csharp
namespace Styloagent.Core.Projects;

/// <summary>Resolved paths for a project's Styloagent state (all under &lt;root&gt;/.styloagent).</summary>
public sealed record ProjectConfig(
    string Root,
    string ConfigDir,
    string SystemPromptPath,
    string ProtocolPath,
    string ChannelRoot,
    string ProposedAgentsPath,
    string LaunchPromptsDir)
{
    /// <summary>Builds the config paths for a project root. Pure — performs no I/O.</summary>
    public static ProjectConfig For(string root)
    {
        string cfg = Path.Combine(root, ".styloagent");
        return new ProjectConfig(
            Root: root,
            ConfigDir: cfg,
            SystemPromptPath: Path.Combine(cfg, "system-prompt.md"),
            ProtocolPath: Path.Combine(cfg, "PROTOCOL.md"),
            ChannelRoot: Path.Combine(cfg, "channel"),
            ProposedAgentsPath: Path.Combine(cfg, "proposed-agents.yaml"),
            LaunchPromptsDir: Path.Combine(cfg, "launch-prompts"));
    }
}
```

Create `src/Styloagent.Core/Projects/DefaultTemplates.cs`:

```csharp
namespace Styloagent.Core.Projects;

/// <summary>Bundled defaults written into a fresh project's .styloagent folder.</summary>
public static class DefaultTemplates
{
    public const string SystemPrompt =
"""
You are the **overview** agent for this codebase. Your job is to understand the shape of the
system and propose the initial team of specialist sub-agents.

Read the repository structure, the key entry points, and the git history. Decide the **3-4**
top-level subsystems that work should be decomposed across. For each, choose a short routing
`prefix` (lowercase, trailing `-`, e.g. `foss-`), a one-line `responsibility`, the `dir` it owns
(relative to the project root, `.` for the root), and a `launchPrompt` that briefs that agent.

Write your proposal to `.styloagent/proposed-agents.yaml` in exactly this schema:

    agents:
      - prefix: foss-
        responsibility: owns the FOSS packages
        dir: .
        launchPrompt: |
          You are the `foss-` agent. You own the FOSS packages. Coordinate over the bus per PROTOCOL.md.

Follow `.styloagent/PROTOCOL.md` for how agents coordinate. Do not spawn agents yourself — the
human reviews your proposal and spawns them.
""";

    public const string Protocol =
"""
# Coordination Protocol

Agents coordinate over a git-backed, file-drop message bus under `.styloagent/channel/`.

- `inbox/<prefix>-<slug>.md` — a message to an agent, awaiting a reply.
- `outbox/<slug>.reply.md` — a reply.
- `archive/` — resolved threads.

Each message begins with `**From:** <prefix>` and `**Timestamp:** <ISO-8601>`. Keep replies sized
to the question. A thread is *replied* once its reply exists; unreplied inbox messages need
attention.

The overview agent proposes the team in `.styloagent/proposed-agents.yaml`; each specialist owns a
responsibility and may later split into more focused agents.
""";
}
```

Create `src/Styloagent.Core/Projects/ProjectScaffolder.cs`:

```csharp
namespace Styloagent.Core.Projects;

/// <summary>
/// Ensures a project's <c>.styloagent</c> tree exists, writing default templates only when absent.
/// Idempotent; never overwrites files the project already has.
/// </summary>
public static class ProjectScaffolder
{
    public static ProjectConfig Ensure(string root)
    {
        var cfg = ProjectConfig.For(root);

        Directory.CreateDirectory(cfg.ConfigDir);
        Directory.CreateDirectory(cfg.LaunchPromptsDir);
        foreach (string sub in new[] { "inbox", "outbox", Path.Combine("archive", "inbox"), Path.Combine("archive", "outbox") })
            Directory.CreateDirectory(Path.Combine(cfg.ChannelRoot, sub));

        if (!File.Exists(cfg.SystemPromptPath))
            File.WriteAllText(cfg.SystemPromptPath, DefaultTemplates.SystemPrompt);
        if (!File.Exists(cfg.ProtocolPath))
            File.WriteAllText(cfg.ProtocolPath, DefaultTemplates.Protocol);

        return cfg;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "ProjectScaffolderTests" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Projects/ProjectConfig.cs src/Styloagent.Core/Projects/DefaultTemplates.cs src/Styloagent.Core/Projects/ProjectScaffolder.cs tests/Styloagent.Core.Tests/ProjectScaffolderTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): project config + scaffold-if-missing .styloagent tree

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Proposed-agents model + reader (Core)

**Files:**
- Create: `src/Styloagent.Core/Projects/ProposedAgent.cs`, `src/Styloagent.Core/Projects/ProposedAgentsReader.cs`
- Test: `tests/Styloagent.Core.Tests/ProposedAgentsReaderTests.cs`

**Interfaces — Produces:**
- `sealed record ProposedAgent(string Prefix, string Responsibility, string Dir, string LaunchPrompt)`
- `static class ProposedAgentsReader { static IReadOnlyList<ProposedAgent> Read(string path); }` — VYaml; tolerant (empty on missing/invalid; never throws).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/ProposedAgentsReaderTests.cs`:

```csharp
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class ProposedAgentsReaderTests
{
    [Fact]
    public void Read_parses_the_agents_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), "pa-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n" +
            "  - prefix: foss-\n" +
            "    responsibility: owns the FOSS packages\n" +
            "    dir: .\n" +
            "    launchPrompt: You are foss-.\n" +
            "  - prefix: dash-\n" +
            "    responsibility: owns the UI\n" +
            "    dir: src/ui\n" +
            "    launchPrompt: You are dash-.\n");
        try
        {
            var agents = ProposedAgentsReader.Read(path);
            Assert.Equal(2, agents.Count);
            Assert.Equal("foss-", agents[0].Prefix);
            Assert.Equal("owns the FOSS packages", agents[0].Responsibility);
            Assert.Equal("src/ui", agents[1].Dir);
            Assert.Equal("You are dash-.", agents[1].LaunchPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_returns_empty_for_missing_or_invalid()
    {
        Assert.Empty(ProposedAgentsReader.Read("/no/such/file.yaml"));

        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(bad, "this: [is not: valid");
        try { Assert.Empty(ProposedAgentsReader.Read(bad)); }
        finally { File.Delete(bad); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "ProposedAgentsReaderTests" --nologo`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Write the implementation**

Create `src/Styloagent.Core/Projects/ProposedAgent.cs`:

```csharp
namespace Styloagent.Core.Projects;

/// <summary>One subsystem the overview agent proposes for the human to spawn.</summary>
public sealed record ProposedAgent(string Prefix, string Responsibility, string Dir, string LaunchPrompt);
```

Create `src/Styloagent.Core/Projects/ProposedAgentsReader.cs`:

```csharp
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

[YamlObject]
internal partial class ProposedAgentsFile
{
    public List<ProposedAgentRow> Agents { get; set; } = new();
}

[YamlObject]
internal partial class ProposedAgentRow
{
    public string Prefix { get; set; } = "";
    public string Responsibility { get; set; } = "";
    public string Dir { get; set; } = ".";
    public string LaunchPrompt { get; set; } = "";
}

/// <summary>Reads <c>proposed-agents.yaml</c> into <see cref="ProposedAgent"/>s. Never throws.</summary>
public static class ProposedAgentsReader
{
    public static IReadOnlyList<ProposedAgent> Read(string path)
    {
        if (!File.Exists(path)) return Array.Empty<ProposedAgent>();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var file = YamlSerializer.Deserialize<ProposedAgentsFile>(new ReadOnlyMemory<byte>(bytes));
            var list = new List<ProposedAgent>();
            foreach (var r in file.Agents)
            {
                if (string.IsNullOrWhiteSpace(r.Prefix)) continue;
                list.Add(new ProposedAgent(r.Prefix.Trim(), r.Responsibility.Trim(),
                    string.IsNullOrWhiteSpace(r.Dir) ? "." : r.Dir.Trim(), r.LaunchPrompt));
            }
            return list;
        }
        catch { return Array.Empty<ProposedAgent>(); }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "ProposedAgentsReaderTests" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Projects/ProposedAgent.cs src/Styloagent.Core/Projects/ProposedAgentsReader.cs tests/Styloagent.Core.Tests/ProposedAgentsReaderTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): ProposedAgent + tolerant proposed-agents.yaml reader

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Recent-projects store (App)

**Files:**
- Create: `src/Styloagent.App/Config/RecentProjectsStore.cs`
- Test: `tests/Styloagent.App.Tests/RecentProjectsStoreTests.cs`

**Interfaces — Produces:** `sealed class RecentProjectsStore` with `Task<IReadOnlyList<string>> LoadAsync(string path)`, `Task AddAsync(string path, string projectRoot)` (most-recent-first, de-duplicated case-sensitively, capped at 8).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/RecentProjectsStoreTests.cs`:

```csharp
using Styloagent.App.Config;
using Xunit;

namespace Styloagent.App.Tests;

public class RecentProjectsStoreTests
{
    [Fact]
    public async Task Add_puts_most_recent_first_dedupes_and_caps()
    {
        var file = Path.Combine(Path.GetTempPath(), "recents-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            var store = new RecentProjectsStore();
            for (int i = 0; i < 10; i++)
                await store.AddAsync(file, "/proj/" + i);
            await store.AddAsync(file, "/proj/3");   // re-add an existing one

            var recents = await store.LoadAsync(file);

            Assert.Equal("/proj/3", recents[0]);        // most-recent first
            Assert.True(recents.Count <= 8);            // capped
            Assert.Single(recents, r => r == "/proj/3"); // de-duplicated
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task Load_returns_empty_when_missing()
        => Assert.Empty(await new RecentProjectsStore().LoadAsync("/no/such/recents.yaml"));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "RecentProjectsStoreTests" --nologo`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Write the implementation**

Create `src/Styloagent.App/Config/RecentProjectsStore.cs`:

```csharp
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.App.Config;

[YamlObject]
internal partial class RecentProjectsFile
{
    public List<string> Projects { get; set; } = new();
}

// CA1822: instance methods by design (mirrors PresentationStore's `new X().M()` usage).
#pragma warning disable CA1822

/// <summary>Persists a capped, de-duplicated, most-recent-first list of project roots (VYaml).</summary>
public sealed class RecentProjectsStore
{
    private const int Cap = 8;

    public async Task<IReadOnlyList<string>> LoadAsync(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var file = YamlSerializer.Deserialize<RecentProjectsFile>(new ReadOnlyMemory<byte>(bytes));
            return file.Projects;
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task AddAsync(string path, string projectRoot)
    {
        var current = (await LoadAsync(path)).ToList();
        current.RemoveAll(p => p == projectRoot);
        current.Insert(0, projectRoot);
        if (current.Count > Cap) current = current.GetRange(0, Cap);

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = YamlSerializer.Serialize(new RecentProjectsFile { Projects = current });
        await File.WriteAllBytesAsync(path, bytes.ToArray());
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "RecentProjectsStoreTests" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Config/RecentProjectsStore.cs tests/Styloagent.App.Tests/RecentProjectsStoreTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): RecentProjectsStore (VYaml, capped, most-recent-first)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: ProposedTeamViewModel (App)

**Files:**
- Create: `src/Styloagent.App/ViewModels/ProposedTeamViewModel.cs`
- Test: `tests/Styloagent.App.Tests/ProposedTeamViewModelTests.cs`

**Interfaces:**
- Consumes: `ProposedAgentsReader.Read`, `ProposedAgent` (Task 2); `PresentationStore.DefaultColorFor`.
- Produces: `ProposedTeamViewModel(string proposedAgentsPath, Action<ProposedAgent> spawn)` with `ObservableCollection<ProposedAgentItem> Proposals`, `void Refresh()`, `[RelayCommand] void Spawn(ProposedAgent)`, `[RelayCommand] void SpawnAll()`. `sealed class ProposedAgentItem { ProposedAgent Agent; string Prefix; string Responsibility; string ColorHex; }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/ProposedTeamViewModelTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class ProposedTeamViewModelTests
{
    [Fact]
    public void Refresh_loads_cards_and_Spawn_invokes_callback()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n  - prefix: foss-\n    responsibility: packages\n    dir: .\n    launchPrompt: hi\n");
        try
        {
            ProposedAgent? spawned = null;
            var vm = new ProposedTeamViewModel(path, a => spawned = a);
            vm.Refresh();

            Assert.Single(vm.Proposals);
            Assert.Equal("foss-", vm.Proposals[0].Prefix);
            Assert.Equal("packages", vm.Proposals[0].Responsibility);

            vm.SpawnCommand.Execute(vm.Proposals[0].Agent);
            Assert.NotNull(spawned);
            Assert.Equal("foss-", spawned!.Prefix);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "ProposedTeamViewModelTests" --nologo`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Write the implementation**

Create `src/Styloagent.App/ViewModels/ProposedTeamViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.Core.Projects;

namespace Styloagent.App.ViewModels;

/// <summary>One proposed subsystem card in the roster's PROPOSED section.</summary>
public sealed class ProposedAgentItem
{
    public ProposedAgent Agent { get; init; } = null!;
    public string Prefix { get; init; } = "";
    public string Responsibility { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";
}

/// <summary>
/// Watches the overview's <c>proposed-agents.yaml</c> and exposes the proposals as cards. Spawning a
/// card hands the <see cref="ProposedAgent"/> to the injected callback (the shell turns it into a
/// live roster agent).
/// </summary>
public sealed partial class ProposedTeamViewModel : ObservableObject, IDisposable
{
    private readonly string _path;
    private readonly Action<ProposedAgent> _spawn;
    private FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private volatile bool _disposed;

    [ObservableProperty]
    private ObservableCollection<ProposedAgentItem> _proposals = new();

    public ProposedTeamViewModel(string proposedAgentsPath, Action<ProposedAgent> spawn)
    {
        _path = proposedAgentsPath;
        _spawn = spawn;
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        Refresh();
        StartWatcher();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var agents = ProposedAgentsReader.Read(_path);
        var items = agents.Select(a => new ProposedAgentItem
        {
            Agent = a,
            Prefix = a.Prefix,
            Responsibility = a.Responsibility,
            ColorHex = PresentationStore.DefaultColorFor(a.Prefix),
        }).ToList();

        void Update()
        {
            Proposals.Clear();
            foreach (var it in items) Proposals.Add(it);
        }

        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    [RelayCommand]
    private void Spawn(ProposedAgent agent)
    {
        _spawn(agent);
        var item = Proposals.FirstOrDefault(p => ReferenceEquals(p.Agent, agent));
        if (item is not null) Proposals.Remove(item);
    }

    [RelayCommand]
    private void SpawnAll()
    {
        foreach (var item in Proposals.ToList())
            Spawn(item.Agent);
    }

    private void StartWatcher()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path)) { EnableRaisingEvents = true };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
        }
        catch { /* degrade gracefully */ }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_disposed) _debounce.Change(200, Timeout.Infinite);
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce.Dispose();
        _watcher?.Dispose();
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "ProposedTeamViewModelTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/ProposedTeamViewModel.cs tests/Styloagent.App.Tests/ProposedTeamViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): ProposedTeamViewModel — watch proposed-agents.yaml, spawn cards

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: MainWindowViewModel — overview launch, SpawnProposed, ProposedTeam wiring

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `ProjectConfig`, `ProposedAgent` (Tasks 1-2); existing `AddAgent` internals.
- Produces on `MainWindowViewModel`: `ProposedTeamViewModel? ProposedTeam { get; }`; `void SpawnProposed(ProposedAgent p)`; `InitializeAsync` gains `string? overviewSystemPromptPath = null`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Styloagent.App.Tests/MainWindowViewModelTests.cs` (append inside the class; add `using Styloagent.Core.Projects;` if absent):

```csharp
    [Fact]
    public async Task SpawnProposed_adds_a_live_pane()
    {
        var root = MakeTwoAgentChannel(); // existing helper: a temp channel dir
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            int before = vm.Panes.Count;

            vm.SpawnProposed(new ProposedAgent("newsub-", "owns the new subsystem", ".", "You are newsub-."));

            Assert.Equal(before + 1, vm.Panes.Count);
            Assert.Contains(vm.Panes, p => p.DisplayName.Contains("newsub"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "SpawnProposed_adds_a_live_pane" --nologo`
Expected: FAIL — `SpawnProposed` doesn't exist.

- [ ] **Step 3: Implement**

3a. Add a field + property near the other observable fields:

```csharp
    private ProjectConfig? _project;

    [ObservableProperty]
    private ProposedTeamViewModel? _proposedTeam;
```

3b. Add `overviewSystemPromptPath` to `InitializeAsync`'s signature (last optional param, before `ct`):

```csharp
    public static async Task<MainWindowViewModel> InitializeAsync(
        string channelRoot,
        IPtyLauncher launcher,
        IFileWatcher watcher,
        IGitReader? gitReader = null,
        string? repoRoot = null,
        string? presentationPath = null,
        string? overviewSystemPromptPath = null,
        CancellationToken ct = default)
```

3c. In `InitializeAsync`, when `overviewSystemPromptPath` is non-null, seed a SINGLE overview entry
and skip worktree/channel seeding. Replace the `entries` selection block's start with:

```csharp
        IReadOnlyList<AgentManifestEntry> entries;
        if (overviewSystemPromptPath is not null)
        {
            string overviewRoot = repoRoot ?? Directory.GetCurrentDirectory();
            entries = new[]
            {
                new AgentManifestEntry(
                    Prefix: "overview-",
                    Repo: overviewRoot,
                    Worktree: overviewRoot,
                    LaunchPromptPath: string.Empty,
                    RestartPromptPath: string.Empty,
                    SavedContextPath: string.Empty,
                    Transport: AgentTransport.Local),
            };
            vm._overviewSystemPromptArgs = File.Exists(overviewSystemPromptPath)
                ? new[] { "--append-system-prompt", File.ReadAllText(overviewSystemPromptPath) }
                : Array.Empty<string>();
        }
        else if (gitReader is not null)
        {
            // …existing worktree branch unchanged…
        }
        else
        {
            // …existing channel-seed branch unchanged…
        }
```

Add the field `private IReadOnlyList<string> _overviewSystemPromptArgs = Array.Empty<string>();` and,
where the FIRST pane's `AgentSession` is built, append these args to the hook args:

```csharp
        var session = new AgentSession(first, launcher, watcher,
            vm.HookArgs(firstHookId).Concat(vm._overviewSystemPromptArgs).ToArray());
```

3d. After the dock factory is built and `vm._project` may be set, wire the proposed team (only in the
bootstrap path — guard on a project config being present). Add a helper the caller sets:

```csharp
    /// <summary>Wires the ProposedTeam VM against a project's proposed-agents.yaml. Idempotent.</summary>
    public void AttachProject(ProjectConfig project)
    {
        _project = project;
        ProposedTeam?.Dispose();
        ProposedTeam = new ProposedTeamViewModel(project.ProposedAgentsPath, SpawnProposed);
    }
```

3e. Add `SpawnProposed`, mirroring `AddAgent` but from a `ProposedAgent`:

```csharp
    /// <summary>Turns a proposed subsystem into a live roster agent (mirrors AddAgent).</summary>
    public void SpawnProposed(ProposedAgent p)
    {
        if (_dockFactory is null || _launcher is null || _watcher is null) return;
        var documentDock = _dockFactory.DocumentDock;
        var rootDock = _dockFactory.RootDock;
        if (documentDock is null || rootDock is null) return;

        // Persist the launch prompt to a file so the existing LaunchPromptPath path can read it.
        string root = _project?.Root ?? DefaultWorkingDirectory();
        string worktree = WorkingDirectoryResolver.Resolve(Path.Combine(root, p.Dir), DefaultWorkingDirectory());
        string launchPromptPath = string.Empty;
        if (_project is not null && !string.IsNullOrWhiteSpace(p.LaunchPrompt))
        {
            Directory.CreateDirectory(_project.LaunchPromptsDir);
            launchPromptPath = Path.Combine(_project.LaunchPromptsDir, SanitizeFileName(p.Prefix) + ".md");
            File.WriteAllText(launchPromptPath, p.LaunchPrompt);
        }

        var entry = new AgentManifestEntry(
            Prefix: p.Prefix, Repo: root, Worktree: worktree,
            LaunchPromptPath: launchPromptPath, RestartPromptPath: string.Empty,
            SavedContextPath: string.Empty, Transport: AgentTransport.Local);

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher, HookArgs(hookId));
        var paneVm = new AgentPaneViewModel(session, entry, p.Prefix.TrimEnd('-'),
            PresentationStore.DefaultColorFor(p.Prefix));
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        var doc = new Document { Id = $"AgentPane-{p.Prefix}", Title = paneVm.DisplayName, Context = paneVm, CanFloat = true };
        _dockFactory.AddDockable(documentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(rootDock, doc);

        _ = paneVm.SpawnAsync();
    }

    private static string SanitizeFileName(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
```

3f. Dispose `ProposedTeam` in `Dispose()`:

```csharp
        ProposedTeam?.Dispose();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Styloagent.App.Tests --nologo`
Expected: PASS — the new test and all existing MainWindowViewModel tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/MainWindowViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): overview launch + SpawnProposed + ProposedTeam wiring

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: WelcomeViewModel + folder picker (App)

**Files:**
- Create: `src/Styloagent.App/Services/IFolderPicker.cs`, `src/Styloagent.App/ViewModels/WelcomeViewModel.cs`
- Test: `tests/Styloagent.App.Tests/WelcomeViewModelTests.cs`

**Interfaces — Produces:**
- `interface IFolderPicker { Task<string?> PickFolderAsync(); }`
- `WelcomeViewModel(RecentProjectsStore recents, string recentsPath, IFolderPicker picker, Action<string> onProjectChosen)` with `ObservableCollection<string> Recent`, `Task LoadRecentsAsync()`, `[RelayCommand] Task OpenFolder()`, `[RelayCommand] void OpenRecent(string path)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/WelcomeViewModelTests.cs`:

```csharp
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

public class WelcomeViewModelTests
{
    private sealed class FakePicker : IFolderPicker
    {
        private readonly string? _result;
        public FakePicker(string? result) => _result = result;
        public Task<string?> PickFolderAsync() => Task.FromResult(_result);
    }

    [Fact]
    public async Task OpenFolder_raises_onProjectChosen_with_the_picked_path()
    {
        string? chosen = null;
        var recentsPath = Path.Combine(Path.GetTempPath(), "wr-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), recentsPath,
                new FakePicker("/picked/project"), p => chosen = p);

            await vm.OpenFolderCommand.ExecuteAsync(null);

            Assert.Equal("/picked/project", chosen);
        }
        finally { if (File.Exists(recentsPath)) File.Delete(recentsPath); }
    }

    [Fact]
    public void OpenRecent_raises_onProjectChosen()
    {
        string? chosen = null;
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker(null), p => chosen = p);

        vm.OpenRecentCommand.Execute("/recent/proj");

        Assert.Equal("/recent/proj", chosen);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "WelcomeViewModelTests" --nologo`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

Create `src/Styloagent.App/Services/IFolderPicker.cs`:

```csharp
namespace Styloagent.App.Services;

/// <summary>Abstracts folder selection so the Welcome VM is testable without a real dialog.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync();
}
```

Create `src/Styloagent.App/ViewModels/WelcomeViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.App.Services;

namespace Styloagent.App.ViewModels;

/// <summary>The startup screen: open a project folder, or reopen a recent one.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly RecentProjectsStore _recents;
    private readonly string _recentsPath;
    private readonly IFolderPicker _picker;
    private readonly Action<string> _onProjectChosen;

    [ObservableProperty]
    private ObservableCollection<string> _recent = new();

    public WelcomeViewModel(RecentProjectsStore recents, string recentsPath, IFolderPicker picker,
        Action<string> onProjectChosen)
    {
        _recents = recents;
        _recentsPath = recentsPath;
        _picker = picker;
        _onProjectChosen = onProjectChosen;
    }

    public async Task LoadRecentsAsync()
    {
        Recent.Clear();
        foreach (var p in await _recents.LoadAsync(_recentsPath))
            Recent.Add(p);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        var path = await _picker.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            _onProjectChosen(path);
    }

    [RelayCommand]
    private void OpenRecent(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _onProjectChosen(path);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Styloagent.App.Tests --filter "WelcomeViewModelTests" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Services/IFolderPicker.cs src/Styloagent.App/ViewModels/WelcomeViewModel.cs tests/Styloagent.App.Tests/WelcomeViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): WelcomeViewModel + IFolderPicker (recents + open)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Views + startup wiring (WelcomeView, roster PROPOSED section, App startup)

**Files:**
- Create: `src/Styloagent.App/Views/WelcomeView.axaml`, `src/Styloagent.App/Views/WelcomeView.axaml.cs`, `src/Styloagent.App/Services/StorageFolderPicker.cs`
- Modify: `src/Styloagent.App/Views/AgentsView.axaml`, `src/Styloagent.App/App.axaml.cs`
- Test: `tests/Styloagent.UITests/WelcomeAndProposedTests.cs`

**Interfaces:**
- Consumes: `WelcomeViewModel` (Task 6), `MainWindowViewModel.ProposedTeam` + `ProposedTeamViewModel`/`ProposedAgentItem` (Tasks 4-5), `ProjectScaffolder`/`ProjectConfig` (Task 1), `RecentProjectsStore` (Task 3).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.UITests/WelcomeAndProposedTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class WelcomeAndProposedTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public WelcomeAndProposedTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakePicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public Task WelcomeView_renders_open_button_and_recents()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml", new FakePicker(), _ => { });
            vm.Recent.Add("/a/recent/project");
            var view = new WelcomeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 380, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, s => s.Contains("Open a project"));
            Assert.Contains(texts, s => s.Contains("/a/recent/project"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-welcome.png");
            window.Close();
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.UITests --filter "WelcomeAndProposedTests" --nologo`
Expected: FAIL — `WelcomeView` doesn't exist.

- [ ] **Step 3: Create `WelcomeView`**

Create `src/Styloagent.App/Views/WelcomeView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Styloagent.App.ViewModels"
             x:Class="Styloagent.App.Views.WelcomeView"
             x:DataType="vm:WelcomeViewModel">
  <Border Background="#0C0C0C" Padding="40">
    <StackPanel Spacing="16" VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="420">
      <TextBlock Text="Styloagent" FontSize="24" FontWeight="Bold" Foreground="#9D7FE0" />
      <Button Content="Open a project folder…" Command="{Binding OpenFolderCommand}"
              Padding="14,8" HorizontalAlignment="Stretch" HorizontalContentAlignment="Center" />
      <TextBlock Text="Recent" FontSize="11" Foreground="#7A7AA0" Margin="0,8,0,0"
                 IsVisible="{Binding Recent.Count}" />
      <ItemsControl ItemsSource="{Binding Recent}">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Button HorizontalAlignment="Stretch" HorizontalContentAlignment="Left" Margin="0,1"
                    Background="#151528" BorderThickness="0"
                    Command="{Binding DataContext.OpenRecentCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                    CommandParameter="{Binding}">
              <TextBlock Text="{Binding}" Foreground="#CCCCDD" FontSize="12" TextTrimming="CharacterEllipsis" />
            </Button>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </StackPanel>
  </Border>
</UserControl>
```

Create `src/Styloagent.App/Views/WelcomeView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Styloagent.App.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();
}
```

- [ ] **Step 4: Add the PROPOSED section to the roster**

In `src/Styloagent.App/Views/AgentsView.axaml`, add — ABOVE the existing agents `ItemsControl`, inside
the `ScrollViewer`'s content panel — a proposed-team section bound through the window's
`MainWindowViewModel.ProposedTeam`. Wrap the roster body in a `StackPanel` if it isn't already, and
insert:

```xml
      <StackPanel IsVisible="{Binding ProposedTeam.Proposals.Count}">
        <Border Background="#2A1A1A" Padding="8,4">
          <Grid ColumnDefinitions="*,Auto">
            <TextBlock Text="PROPOSED" FontSize="10" FontWeight="SemiBold" Foreground="#E5A05A" LetterSpacing="1" />
            <Button Grid.Column="1" Content="Spawn all" FontSize="10" Padding="6,1"
                    Command="{Binding ProposedTeam.SpawnAllCommand}" />
          </Grid>
        </Border>
        <ItemsControl ItemsSource="{Binding ProposedTeam.Proposals}">
          <ItemsControl.ItemTemplate>
            <DataTemplate DataType="vm:ProposedAgentItem">
              <Border Background="#111122" Margin="0,1,0,0">
                <Grid ColumnDefinitions="4,*,Auto">
                  <Border Grid.Column="0" Background="{Binding ColorHex}" Width="4" />
                  <StackPanel Grid.Column="1" Margin="8,5" Spacing="1">
                    <TextBlock Text="{Binding Prefix}" FontWeight="SemiBold" FontSize="12" Foreground="{Binding ColorHex}" />
                    <TextBlock Text="{Binding Responsibility}" FontSize="10" Foreground="#8888AA" TextTrimming="CharacterEllipsis" />
                  </StackPanel>
                  <Button Grid.Column="2" Content="Spawn" FontSize="10" Margin="0,0,6,0" VerticalAlignment="Center"
                          Command="{Binding DataContext.ProposedTeam.SpawnCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                          CommandParameter="{Binding Agent}" />
                </Grid>
              </Border>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </StackPanel>
```

(The `AgentsView` `x:DataType` is `MainWindowViewModel`, so `ProposedTeam` resolves; add
`xmlns:vm` if not present.)

- [ ] **Step 5: Real folder picker + welcome-first startup**

Create `src/Styloagent.App/Services/StorageFolderPicker.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Styloagent.App.Services;

/// <summary>Real folder picker backed by a window's StorageProvider.</summary>
public sealed class StorageFolderPicker : IFolderPicker
{
    private readonly TopLevel _topLevel;
    public StorageFolderPicker(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFolderAsync()
    {
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Open a project folder" });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
```

In `src/Styloagent.App/App.axaml.cs` `OnFrameworkInitializationCompleted`, use a **separate Welcome
window** shown first; opening a project builds the cockpit `MainWindow` and closes the Welcome
window. Do NOT swap `MainWindow`'s content (that would clobber its cockpit XAML). Keep the
`STYLOAGENT_REPO` shortcut (open it directly, no Welcome):

```csharp
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string recentsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Styloagent", "recent-projects.yaml");
            var recents = new RecentProjectsStore();

            async Task OpenProjectAsync(string root, Window? welcomeWindow)
            {
                var cfg = Styloagent.Core.Projects.ProjectScaffolder.Ensure(root);
                await recents.AddAsync(recentsPath, root);
                var vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new PortaPtyLauncher(), new FileSystemFileWatcher(),
                    repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath);
                vm.AttachProject(cfg);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var cockpit = new MainWindow { DataContext = vm };
                    desktop.MainWindow = cockpit;
                    cockpit.Show();
                    welcomeWindow?.Close();
                });
            }

            var repoEnv = Environment.GetEnvironmentVariable("STYLOAGENT_REPO");
            if (!string.IsNullOrWhiteSpace(repoEnv))
            {
                _ = OpenProjectAsync(repoEnv, welcomeWindow: null);
            }
            else
            {
                var welcomeWindow = new Window { Title = "Styloagent", Width = 520, Height = 380 };
                var welcome = new WelcomeViewModel(recents, recentsPath,
                    new StorageFolderPicker(welcomeWindow),
                    root => _ = OpenProjectAsync(root, welcomeWindow));
                welcomeWindow.Content = new WelcomeView { DataContext = welcome };
                desktop.MainWindow = welcomeWindow;
                welcomeWindow.Show();
                _ = welcome.LoadRecentsAsync();
            }

            desktop.ShutdownRequested += (_, _) => (desktop.MainWindow?.DataContext as IDisposable)?.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
```

Add any missing `using` directives (`Avalonia.Controls`, `Styloagent.App.Config`,
`Styloagent.App.Services`, `Styloagent.App.ViewModels`, `Styloagent.App.Views`,
`Styloagent.Terminal`).

- [ ] **Step 6: Run tests + build**

Run: `dotnet build --nologo` → fix any `error CA####` (index access, static-readonly arrays).
Run: `dotnet test tests/Styloagent.UITests --filter "WelcomeAndProposedTests" --nologo` → PASS.
Run: `dotnet test --nologo` → all suites green.
View `/tmp/styloagent-welcome.png` to confirm the Welcome screen renders.

- [ ] **Step 7: Commit**

```bash
git add src/Styloagent.App/Views/WelcomeView.axaml src/Styloagent.App/Views/WelcomeView.axaml.cs src/Styloagent.App/Services/StorageFolderPicker.cs src/Styloagent.App/Views/AgentsView.axaml src/Styloagent.App/App.axaml.cs tests/Styloagent.UITests/WelcomeAndProposedTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bootstrap): Welcome screen + roster PROPOSED section + welcome-first startup

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes / follow-ups (not this plan)

- **MCP slice (next):** the Styloagent MCP server + `spawn_agent`, so the overview *starts* the team
  itself and subsystems split recursively.
- Verify Claude Code's `--append-system-prompt` flag during Task 5/7; fallback is to prepend the
  system prompt to the overview's first-message launch prompt.
- README demo: add a Welcome + PROPOSED screenshot to `ReadmeScreenshotTests` once landed.
