using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;
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

    // ── Hook state badge (§4.4) ───────────────────────────────────────────────

    [Fact]
    public void InitialHookState_Is_Unknown()
    {
        var vm = MakeVm();
        Assert.Equal(AgentHookState.Unknown, vm.HookState);
        Assert.False(vm.NeedsYou);
    }

    [Fact]
    public void ApplyHookEvent_PermissionNotification_Sets_NeedsYou_Badge()
    {
        var vm = MakeVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.ApplyHookEvent(new HookEvent("foss", "Notification", "permission_prompt", "Allow?", null, null));

        Assert.Equal(AgentHookState.WaitingForHuman, vm.HookState);
        Assert.True(vm.NeedsYou);
        Assert.Equal("needs you", vm.HookStateText);
        Assert.Equal("⚠", vm.HookStateGlyph);
        Assert.Equal("#FFCC33", vm.HookStateColorHex);
        Assert.Equal("#3A2E00", vm.RowHighlightHex);
        // The dependent badge properties must raise change notifications so the roster updates.
        Assert.Contains(nameof(vm.NeedsYou), changed);
        Assert.Contains(nameof(vm.HookStateText), changed);
        Assert.Contains(nameof(vm.HookStateColorHex), changed);
    }

    [Fact]
    public void SelectionBrushHex_IsIdentityColorWhenSelected_TransparentOtherwise()
    {
        var vm = MakeVm(borderColor: "#ABCDEF");
        Assert.False(vm.IsSelected);
        Assert.Equal("#00000000", vm.SelectionBrushHex);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.IsSelected = true;

        Assert.Equal("#ABCDEF", vm.SelectionBrushHex);
        Assert.Contains(nameof(vm.SelectionBrushHex), changed);
    }

    [Fact]
    public void ApplyHookEvent_WorkingThenIdle_Tracks_State()
    {
        var vm = MakeVm();

        vm.ApplyHookEvent(new HookEvent("foss", "PreToolUse", null, null, null, null));
        Assert.Equal(AgentHookState.Working, vm.HookState);
        Assert.Equal("working", vm.HookStateText);
        Assert.False(vm.NeedsYou);

        vm.ApplyHookEvent(new HookEvent("foss", "Notification", "idle_prompt", null, null, null));
        Assert.Equal(AgentHookState.Idle, vm.HookState);
        Assert.Equal("idle", vm.HookStateText);
        Assert.Equal("#111122", vm.RowHighlightHex);
    }
}
