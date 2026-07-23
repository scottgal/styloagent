using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Styloagent.Core.Sessions;
using Styloagent.Core.Config;

namespace Styloagent.App.Tests;

public class MainWindowViewModelTests : IDisposable
{
    private readonly string _channelRoot;

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 10)
            await Task.Delay(10);
    }

    public MainWindowViewModelTests()
    {
        // Set up a minimal channel directory with ONE agent context file.
        _channelRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(_channelRoot, "saved-context");
        Directory.CreateDirectory(savedContext);
        File.WriteAllText(Path.Combine(savedContext, "foss-context.md"), "# FOSS context");
    }

    public void Dispose()
    {
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public async Task Initialize_WithOneContextFile_ExposesOnePaneForFirstAgent()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        Assert.NotNull(vm.Pane);
    }

    [Fact]
    public async Task Initialize_Pane_HasCorrectPrefix()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        // DisplayName defaults to prefix with trailing '-' trimmed.
        Assert.Equal("foss", vm.Pane!.DisplayName);
    }

    [Fact]
    public async Task Initialize_Pane_HasBorderColorHex()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        // Colour is derived deterministically from prefix and must be a hex string.
        Assert.NotNull(vm.Pane!.BorderColorHex);
        Assert.StartsWith("#", vm.Pane.BorderColorHex);
    }

    [Fact]
    public async Task ApprovePermission_uses_runtime_specific_confirmation_input()
    {
        var codexLauncher = new FakeLauncher();
        var codex = await MainWindowViewModel.InitializeAsync(
            _channelRoot, codexLauncher, new FakeWatcher(), defaultAgentRuntime: AgentRuntimeKind.Codex);
        codex.Pane!.ApplyHookEvent(new Styloagent.Core.Hooks.HookEvent(
            "foss-", "PermissionRequest", null, "Approve?", "session", "/repo"));
        codex.ApprovePermissionCommand.Execute(codex.Pane);
        Assert.Contains("\r", codexLauncher.Spawned.Single().Writes);
        Assert.DoesNotContain("1\r", codexLauncher.Spawned.Single().Writes);
        Assert.Equal(Styloagent.Core.Hooks.AgentHookState.Working, codex.Pane.HookState);

        var claudeRoot = MakeTwoAgentChannel();
        try
        {
            var claudeLauncher = new FakeLauncher();
            var claude = await MainWindowViewModel.InitializeAsync(claudeRoot, claudeLauncher, new FakeWatcher());
            claude.Pane!.ApplyHookEvent(new Styloagent.Core.Hooks.HookEvent(
                "foss-", "PermissionRequest", null, "Approve?", "session", "/repo"));
            claude.ApprovePermissionCommand.Execute(claude.Pane);
            Assert.Contains("1\r", claudeLauncher.Spawned.Single().Writes);
        }
        finally { Directory.Delete(claudeRoot, recursive: true); }
    }

    [Fact]
    public async Task Initialize_WithStoredPresentation_UsesStoredDisplayName()
    {
        // Write a presentation sidecar with a custom name for foss-.
        var presentationPath = Path.Combine(_channelRoot, "presentation.yaml");
        var store = new Styloagent.App.Config.PresentationStore();
        await store.SaveAsync(presentationPath,
        [
            new("foss-", "My FOSS Agent", "#FF0000"),
        ]);

        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher(),
            presentationPath: presentationPath);

        Assert.Equal("My FOSS Agent", vm.Pane!.DisplayName);
        Assert.Equal("#FF0000", vm.Pane.BorderColorHex);
    }

    [Fact]
    public async Task Initialize_EmptyChannel_PaneIsNull()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        // Don't create the saved-context directory.
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                emptyRoot,
                new FakeLauncher(),
                new FakeWatcher());

            Assert.Null(vm.Pane);
        }
        finally
        {
            if (Directory.Exists(emptyRoot))
                Directory.Delete(emptyRoot, recursive: true);
        }
    }

    // ── Multi-pane / AddAgent tests ───────────────────────────────────────────

    /// <summary>
    /// Sets up a channel with TWO saved-context files so we can test seeded-entry promotion.
    /// </summary>
    internal static string MakeTwoAgentChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var ctx = Path.Combine(root, "saved-context");
        Directory.CreateDirectory(ctx);
        File.WriteAllText(Path.Combine(ctx, "alpha-context.md"), "# alpha");
        File.WriteAllText(Path.Combine(ctx, "beta-context.md"), "# beta");
        return root;
    }

    [Fact]
    public async Task Initialize_WithTwoContextFiles_DocumentDockStartsWithOneDocument()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                root, new FakeLauncher(), new FakeWatcher());

            var dock = vm.DocumentDock;
            Assert.NotNull(dock);
            Assert.Equal(1, dock!.VisibleDockables?.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AddAgentCommand_AddsSecondSeededEntryToDocumentDock()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                root, new FakeLauncher(), new FakeWatcher());

            vm.AddAgentCommand.Execute(null);

            var dock = vm.DocumentDock;
            Assert.NotNull(dock);
            Assert.Equal(2, dock!.VisibleDockables?.Count);

            // The pane VM IS the Dock document (it inherits Document) — the dockable itself is the pane.
            var secondDoc = dock.VisibleDockables!
                .OfType<Document>()
                .ElementAt(1);
            Assert.IsType<AgentPaneViewModel>(secondDoc);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AddCodexCommand_AddsPaneAndLaunchesCodex()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            var vm = await MainWindowViewModel.InitializeAsync(
                root, launcher, new FakeWatcher());

            vm.AddCodexCommand.Execute(null);
            await WaitUntil(() => launcher.Options.Count >= 2);

            Assert.Equal(2, vm.Panes.Count);
            Assert.Equal(AgentRuntimeKind.Codex, vm.Panes[1].Runtime);
            Assert.StartsWith("agent-", vm.Panes[1].Prefix);
            Assert.Equal("New Codex", vm.Panes[1].DisplayName);
            Assert.Equal("codex", launcher.Options[1].Command);
            Assert.Contains("--config", launcher.Options[1].Args);
            Assert.Contains(launcher.Options[1].Args, a => a.Contains("hooks.SessionStart", StringComparison.Ordinal));
            Assert.DoesNotContain(launcher.Options[1].Args, a => a == "--settings");
            Assert.DoesNotContain(launcher.Options[1].Args, a => a == "--mcp-config");
            Assert.DoesNotContain(launcher.Options[1].Args, a => a.Contains("You are", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AddClaudeCommand_opens_blank_generic_agent_without_inheriting_seeded_name()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            var vm = await MainWindowViewModel.InitializeAsync(root, launcher, new FakeWatcher());

            vm.AddClaudeCommand.Execute(null);
            await WaitUntil(() => launcher.Options.Count >= 2);

            Assert.StartsWith("agent-", vm.Panes[1].Prefix);
            Assert.Equal("New Claude", vm.Panes[1].DisplayName);
            Assert.DoesNotContain(launcher.Spawned[1].Writes, w => w.Contains("You are", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RenameAgent_updates_tab_identity_and_broadcasts_stable_prefix_mapping()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var pane = vm.Panes[0];
            var prefix = pane.Prefix;

            var result = await vm.RenameAgentAsync(prefix, "Planner");

            Assert.Contains("renamed", result);
            Assert.Equal("Planner", pane.DisplayName);
            Assert.Equal("Planner", pane.Title);
            Assert.Contains(vm.Timeline.Entries, e => e.Description.Contains("renamed from"));
            Assert.Contains(Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories), path =>
                File.ReadAllText(path).Contains("Agent " + prefix + " is now named 'Planner'"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DefaultRuntimeCodex_AddAgentCommandAndSpawnChildLaunchCodex()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            var vm = await MainWindowViewModel.InitializeAsync(
                root, launcher, new FakeWatcher(), defaultAgentRuntime: AgentRuntimeKind.Codex);

            await WaitUntil(() => launcher.Options.Count >= 1);
            Assert.Equal("codex", launcher.Options[0].Command);

            vm.AddAgentCommand.Execute(null);
            await WaitUntil(() => launcher.Options.Count >= 2);
            Assert.Equal(AgentRuntimeKind.Codex, vm.Panes[1].Runtime);
            Assert.Equal("codex", launcher.Options[1].Command);

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                vm.Panes[0].Prefix, "child-", "r", ".", "p", Worktree: false));
            await WaitUntil(() => launcher.Options.Count >= 3);

            Assert.True(outcome.Spawned);
            Assert.Equal(AgentRuntimeKind.Codex, vm.Panes[2].Runtime);
            Assert.Equal("codex", launcher.Options[2].Command);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OverviewRevival_PreservesPersistedRuntimeForParkedAgent()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        var promptPath = Path.Combine(repoRoot, "system-prompt.md");
        await File.WriteAllTextAsync(promptPath, "overview instructions");
        try
        {
            var saved = Path.Combine(_channelRoot, "saved-context", "foss-context.md");
            await new ManifestStore().SaveAsync(Path.Combine(_channelRoot, "agents.yaml"),
            [
                new AgentManifestEntry("foss-", repoRoot, repoRoot, "", "", saved,
                    AgentTransport.Local, AgentRuntimeKind.Codex),
            ]);
            var launcher = new FakeLauncher();

            var vm = await MainWindowViewModel.InitializeAsync(
                _channelRoot, launcher, new FakeWatcher(), repoRoot: repoRoot,
                overviewSystemPromptPath: promptPath, defaultAgentRuntime: AgentRuntimeKind.Claude);
            await WaitUntil(() => launcher.Options.Count >= 1);

            vm.AddAgentCommand.Execute(null);
            await WaitUntil(() => launcher.Options.Count >= 2);

            Assert.Equal(AgentRuntimeKind.Codex, vm.Panes[1].Runtime);
            Assert.Equal("codex", launcher.Options[1].Command);
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task SpawnChild_RuntimeRequestCanOverrideDefaultForMixedFleet()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            var vm = await MainWindowViewModel.InitializeAsync(
                root, launcher, new FakeWatcher(), defaultAgentRuntime: AgentRuntimeKind.Codex);
            await WaitUntil(() => launcher.Options.Count >= 1);

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                vm.Panes[0].Prefix, "claude-child-", "r", ".", "p", Worktree: false, Runtime: "claude"));
            await WaitUntil(() => launcher.Options.Count >= 2);

            Assert.True(outcome.Spawned);
            Assert.Equal(AgentRuntimeKind.Claude, vm.Panes[1].Runtime);
            Assert.Equal("claude", launcher.Options[1].Command);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CodexPane_StopHookMarksIdle()
    {
        var entry = new AgentManifestEntry(
            "codex-", "/repo", "/repo", "", "", "", AgentTransport.Local, AgentRuntimeKind.Codex);
        var pane = new AgentPaneViewModel(
            new AgentSession(entry, new FakeLauncher(), new FakeWatcher(), Array.Empty<string>()),
            entry, "codex", "#FF6666");

        pane.ApplyHookEvent(new Styloagent.Core.Hooks.HookEvent(
            "codex-", "SessionStart", null, null, "s", "/repo"));
        Assert.Equal(Styloagent.Core.Hooks.AgentHookState.Working, pane.HookState);

        pane.ApplyHookEvent(new Styloagent.Core.Hooks.HookEvent(
            "codex-", "Stop", null, null, "s", "/repo"));

        Assert.Equal(Styloagent.Core.Hooks.AgentHookState.Idle, pane.HookState);
    }

    [Fact]
    public async Task SelectPane_MarksOnlyTheSelectedPaneAsSelected()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                root, new FakeLauncher(), new FakeWatcher());

            // The first pane is selected on init.
            var first = vm.Panes[0];
            Assert.True(first.IsSelected);

            vm.AddAgentCommand.Execute(null);
            var second = vm.Panes[1];

            // Adding a pane selects it and deselects the first.
            Assert.True(second.IsSelected);
            Assert.False(first.IsSelected);

            // Selecting back flips it again — only one pane is ever selected.
            vm.SelectPaneCommand.Execute(first);
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AddAgentCommand_ThirdCall_FallsBackToGenericAgent()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                root, new FakeLauncher(), new FakeWatcher());

            // Add second seeded entry
            vm.AddAgentCommand.Execute(null);
            // All seeded entries now open — next call synthesizes a generic entry
            vm.AddAgentCommand.Execute(null);

            var dock = vm.DocumentDock;
            Assert.NotNull(dock);
            Assert.Equal(3, dock!.VisibleDockables?.Count);

            var thirdDoc = dock.VisibleDockables!
                .OfType<Document>()
                .ElementAt(2);
            var thirdPaneVm = Assert.IsType<AgentPaneViewModel>(thirdDoc);
            Assert.StartsWith("agent-", thirdPaneVm.DisplayName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── Overview-launch path tests ────────────────────────────────────────────

    [Fact]
    public async Task OverviewPath_ExistingSystemPromptFile_SeedsExactlyOneOverviewPane()
    {
        // Arrange: a temp dir to act as repoRoot and a temp system-prompt file with known content.
        var repoRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        var promptFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md");
        const string promptContent = "You are the overview agent.";
        File.WriteAllText(promptFile, promptContent);

        var launcher = new FakeLauncher();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                _channelRoot,
                launcher,
                new FakeWatcher(),
                repoRoot: repoRoot,
                overviewSystemPromptPath: promptFile);

            // Exactly one pane, and it is the overview- agent.
            Assert.Single(vm.Panes);
            Assert.NotNull(vm.Pane);
            Assert.Contains("overview", vm.Pane!.DisplayName);

            // FakeLauncher captures PtySpawnOptions synchronously (Task.FromResult),
            // so the spawn args are observable here.
            Assert.Single(launcher.Options);
            var args = launcher.Options[0].Args.ToList();
            var appendIdx = args.IndexOf("--append-system-prompt");
            Assert.True(appendIdx >= 0, "Expected --append-system-prompt arg to be present");
            Assert.Equal(promptContent, args[appendIdx + 1]);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            File.Delete(promptFile);
        }
    }

    [Fact]
    public async Task OverviewPath_MissingSystemPromptFile_DoesNotThrow_SeedsOneOverviewPane_NoAppendArgs()
    {
        // Arrange: a non-existent system-prompt file path.
        var repoRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        var missingPromptFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-missing.md");
        // Intentionally do NOT create missingPromptFile.

        var launcher = new FakeLauncher();
        try
        {
            // Must not throw even though the file does not exist.
            var vm = await MainWindowViewModel.InitializeAsync(
                _channelRoot,
                launcher,
                new FakeWatcher(),
                repoRoot: repoRoot,
                overviewSystemPromptPath: missingPromptFile);

            // Still seeds exactly one overview pane.
            Assert.Single(vm.Panes);
            Assert.NotNull(vm.Pane);
            Assert.Contains("overview", vm.Pane!.DisplayName);

            // No --append-system-prompt arg should be present because the file was missing.
            Assert.Single(launcher.Options);
            var args = launcher.Options[0].Args.ToList();
            Assert.DoesNotContain("--append-system-prompt", args);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
            // missingPromptFile was never created, nothing to delete.
        }
    }
}
