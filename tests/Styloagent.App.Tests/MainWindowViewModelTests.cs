using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;

namespace Styloagent.App.Tests;

public class MainWindowViewModelTests : IDisposable
{
    private readonly string _channelRoot;

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

    [Fact]
    public async Task SpawnProposed_adds_a_live_pane()
    {
        var root = MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            int before = vm.Panes.Count;

            await vm.SpawnProposedAsync(new ProposedAgent("newsub-", "owns the new subsystem", ".", "You are newsub-."));

            Assert.Equal(before + 1, vm.Panes.Count);
            Assert.Contains(vm.Panes, p => p.DisplayName.Contains("newsub"));
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
