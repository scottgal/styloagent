using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Tests;

public class AgentPaneViewModelTests
{
    private static AgentManifestEntry MakeEntry(
        string launchPromptPath = "",
        string restartPromptPath = "",
        string savedContextPath = "") =>
        new(
            Prefix: "foss-",
            Repo: "/repo",
            Worktree: "/repo/wt-foss",
            LaunchPromptPath: launchPromptPath,
            RestartPromptPath: restartPromptPath,
            SavedContextPath: savedContextPath,
            Transport: AgentTransport.Local);

    private static AgentPaneViewModel MakeVm(
        AgentManifestEntry? entry = null,
        FakeLauncher? launcher = null,
        FakeWatcher? watcher = null,
        string displayName = "foss",
        string borderColor = "#E57373")
    {
        entry ??= MakeEntry();
        launcher ??= new FakeLauncher();
        watcher ??= new FakeWatcher();
        var session = new AgentSession(entry, launcher, watcher);
        return new AgentPaneViewModel(session, entry, displayName, borderColor);
    }

    [Fact]
    public async Task SpawnCommand_Sets_State_Live()
    {
        var vm = MakeVm();

        await vm.SpawnAsync();

        Assert.Equal(SessionState.Live, vm.State);
    }

    [Fact]
    public async Task DehydrateCommand_WithAck_Sets_State_Dehydrated()
    {
        var watcher = new FakeWatcher { WillChange = true };
        var vm = MakeVm(watcher: watcher);

        await vm.SpawnAsync();
        await vm.DehydrateAsync();

        Assert.Equal(SessionState.Dehydrated, vm.State);
    }

    [Fact]
    public async Task DehydrateCommand_WithoutAck_State_Stays_Live()
    {
        var watcher = new FakeWatcher { WillChange = false };
        var vm = MakeVm(watcher: watcher);

        await vm.SpawnAsync();
        await vm.DehydrateAsync();

        // watcher returned false → session stays Live; context must not be lost.
        Assert.Equal(SessionState.Live, vm.State);
    }

    [Fact]
    public async Task RehydrateCommand_After_Dehydrate_Sets_State_Live()
    {
        var watcher = new FakeWatcher { WillChange = true };
        var vm = MakeVm(watcher: watcher);

        await vm.SpawnAsync();
        await vm.DehydrateAsync();
        Assert.Equal(SessionState.Dehydrated, vm.State);

        await vm.RehydrateAsync();

        Assert.Equal(SessionState.Live, vm.State);
    }

    [Fact]
    public async Task SpawnCommand_ReadsPromptFromFile_WhenPathExists()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "CUSTOM LAUNCH PROMPT");
            var entry = MakeEntry(launchPromptPath: tmp);
            var launcher = new FakeLauncher();
            var vm = MakeVm(entry: entry, launcher: launcher);

            await vm.SpawnAsync();

            Assert.Contains(launcher.Spawned[0].Writes, w => w.Contains("CUSTOM LAUNCH PROMPT"));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task SpawnCommand_UsesFallbackPrompt_WhenPathMissing()
    {
        var entry = MakeEntry(launchPromptPath: "/nonexistent/path.md");
        var launcher = new FakeLauncher();
        var vm = MakeVm(entry: entry, launcher: launcher);

        await vm.SpawnAsync();

        // Should not throw; state is Live with a fallback prompt.
        Assert.Equal(SessionState.Live, vm.State);
        Assert.Single(launcher.Spawned);
    }

    [Fact]
    public void RenameCommand_Updates_DisplayName()
    {
        var vm = MakeVm(displayName: "original");

        vm.Rename("renamed");

        Assert.Equal("renamed", vm.DisplayName);
    }

    [Fact]
    public void InitialState_Is_Unspawned()
    {
        var vm = MakeVm();
        Assert.Equal(SessionState.Unspawned, vm.State);
    }

    [Fact]
    public void Constructor_SetsDisplayNameAndBorderColor()
    {
        var vm = MakeVm(displayName: "FOSS Agent", borderColor: "#ABCDEF");
        Assert.Equal("FOSS Agent", vm.DisplayName);
        Assert.Equal("#ABCDEF", vm.BorderColorHex);
    }
}
