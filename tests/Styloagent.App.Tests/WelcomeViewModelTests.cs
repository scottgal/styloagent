using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
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
    public void Constructor_applies_the_initial_runtime()
    {
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker(null), _ => { }, initialRuntime: AgentRuntimeKind.Codex);

        Assert.Equal(AgentRuntimeKind.Codex, vm.SelectedRuntime);
        Assert.True(vm.IsCodexFirst);
    }

    [Fact]
    public void SetRuntimeMode_raises_onRuntimeChanged_with_the_parsed_runtime()
    {
        AgentRuntimeKind? changed = null;
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker(null), _ => { },
            initialRuntime: AgentRuntimeKind.Claude,
            onRuntimeChanged: kind => changed = kind);

        vm.SetRuntimeModeCommand.Execute("Kilo");

        Assert.Equal(AgentRuntimeKind.Kilo, vm.SelectedRuntime);
        Assert.Equal(AgentRuntimeKind.Kilo, changed);
        Assert.True(vm.IsKiloFirst);
        Assert.False(vm.IsClaudeFirst);
    }

    [Fact]
    public void SetRuntimeMode_on_already_selected_card_keeps_it_visually_selected()
    {
        // Clicking the already-selected card must not leave the selector with nothing selected:
        // the OneWay Is* bindings are re-pushed even when the source value is unchanged.
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker(null), _ => { }, initialRuntime: AgentRuntimeKind.Kilo);

        vm.SetRuntimeModeCommand.Execute("Kilo");

        Assert.Equal(AgentRuntimeKind.Kilo, vm.SelectedRuntime);
        Assert.True(vm.IsKiloFirst);
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

    [Fact]
    public async Task NewSystem_scaffolds_the_folder_writes_the_brief_and_opens_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "newsys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? chosen = null;
        try
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
                new FakePicker(root), p => chosen = p)
            {
                NewSystemDescription = "a system like Trello which manages kanban boards",
            };

            await vm.NewSystemCommand.ExecuteAsync(null);

            var briefPath = Path.Combine(root, ".styloagent", "brief.md");
            Assert.True(File.Exists(briefPath));
            var brief = await File.ReadAllTextAsync(briefPath);
            Assert.Contains("Trello", brief);
            Assert.Contains("clarifying questions", brief, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(root, chosen);           // opened the new project
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewSystem_with_blank_description_does_nothing()
    {
        string? chosen = null;
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker("/should/not/be/used"), p => chosen = p);

        await vm.NewSystemCommand.ExecuteAsync(null);

        Assert.Null(chosen);
    }
}
