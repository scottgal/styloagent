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
        string savedContextPath = "/repo/wt-foss/.context.md") =>
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
    public async Task DehydrateCommand_WithNoSavedContextPath_StaysLive_AndDoesNotThrow()
    {
        // An agent with no checkpoint target (e.g. the overview agent) cannot dehydrate. This must be
        // a safe no-op — not an empty-path ArgumentException that crashes the app (regression guard).
        var vm = MakeVm(entry: MakeEntry(savedContextPath: ""),
            watcher: new FakeWatcher { WillChange = true });

        await vm.SpawnAsync();
        await vm.DehydrateAsync();

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

    // ── Optimistic badge clear on in-terminal answer (roster-badge-slow-to-update fix) ──────────

    [Fact]
    public void NoteTerminalInteraction_WhileWaiting_FlipsBadgeToWorking_WithoutAHookEvent()
    {
        var vm = MakeVm();
        // The agent is blocked on the human — amber ⚠ "needs you" badge.
        vm.ApplyHookEvent(new HookEvent("foss", "Notification", "permission_prompt", "Allow?", null, null));
        vm.WaitingSince = DateTimeOffset.UtcNow;
        Assert.True(vm.NeedsYou);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        // The operator answers in-terminal. That interaction IS the answer, so the badge must flip to
        // "working" immediately — not linger amber until the next hook event lands.
        bool flipped = vm.NoteTerminalInteraction();

        Assert.True(flipped);
        Assert.Equal(AgentHookState.Working, vm.HookState);
        Assert.False(vm.NeedsYou);
        Assert.Equal("●", vm.HookStateGlyph);
        Assert.Equal("#57A64A", vm.HookStateColorHex);
        Assert.Equal("", vm.WaitingQuestion);
        Assert.Null(vm.WaitingSince);
        // Roster badge reacts to PropertyChanged (no full reload).
        Assert.Contains(nameof(vm.NeedsYou), changed);
        Assert.Contains(nameof(vm.HookStateGlyph), changed);
        Assert.Contains(nameof(vm.HookStateColorHex), changed);
    }

    [Fact]
    public void NoteTerminalInteraction_WhenNotWaiting_IsNoOp()
    {
        var vm = MakeVm();
        vm.ApplyHookEvent(new HookEvent("foss", "PreToolUse", null, null, null, null));
        Assert.Equal(AgentHookState.Working, vm.HookState);

        bool flipped = vm.NoteTerminalInteraction();

        Assert.False(flipped);
        Assert.Equal(AgentHookState.Working, vm.HookState);
    }

    // ── Pane-chrome zoom relay (0b) ────────────────────────────────────────────

    [Fact]
    public void ZoomLevel_DefaultsToOne_AndClampsToBounds()
    {
        var vm = MakeVm();
        Assert.Equal(1.0, vm.ZoomLevel);

        vm.ZoomLevel = 5.0;                  // above max (3.0)
        Assert.Equal(3.0, vm.ZoomLevel);

        vm.ZoomLevel = 0.1;                  // below min (0.5)
        Assert.Equal(0.5, vm.ZoomLevel);

        vm.ZoomLevel = 1.5;                  // in range
        Assert.Equal(1.5, vm.ZoomLevel);
    }

    // ── Agent-log "Log (this agent)" entry (item 1) ────────────────────────────

    [Fact]
    public void OpenLog_IsSafe_WhenNoHostAttached()
    {
        // The chrome dropdown's "Log (this agent)" command routes through Host; with no host (unhosted /
        // test), it must be a harmless no-op, never throw.
        var vm = MakeVm();
        Assert.Null(vm.Host);
        var ex = Record.Exception(() => vm.OpenLogCommand.Execute(null));
        Assert.Null(ex);
    }
}
