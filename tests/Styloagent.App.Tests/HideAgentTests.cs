using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class HideAgentTests
{
    [Fact]
    public async Task HideAgent_takes_the_pane_off_screen_but_keeps_it_running_in_the_roster()
    {
        var repo = Path.Combine(Path.GetTempPath(), "hide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var cfg = ProjectScaffolder.Ensure(repo);
            using var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);

            var pane = vm.Panes[0];
            Assert.False(pane.IsHidden);
            Assert.NotEqual(SessionState.Dehydrated, pane.State);

            vm.HideAgentCommand.Execute(pane);

            Assert.True(pane.IsHidden);                              // marked hidden (off-screen)
            Assert.Contains(pane, vm.Panes);                        // still in the roster — not torn down
            Assert.Contains(pane, vm.InactiveAgents);               // compact inactive section owns the row
            Assert.DoesNotContain(vm.RosterGroups.SelectMany(g => g.Agents), p => p == pane);
            Assert.NotEqual(SessionState.Dehydrated, pane.State);   // still RUNNING — hide != dehydrate

            vm.ShowAgentCommand.Execute(pane);

            Assert.False(pane.IsHidden);                            // restored, no rehydrate needed
            Assert.Contains(pane, vm.Panes);
            Assert.DoesNotContain(pane, vm.InactiveAgents);
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task Selecting_a_hidden_agent_in_the_roster_reopens_its_pane()
    {
        var repo = Path.Combine(Path.GetTempPath(), "reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var cfg = ProjectScaffolder.Ensure(repo);
            using var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);

            var pane = vm.Panes[0];
            vm.HideAgentCommand.Execute(pane);   // removes the dockable (a closed tab is the same shape)
            Assert.True(pane.IsHidden);

            vm.SelectPaneCommand.Execute(pane);  // a roster click must bring it back

            Assert.False(pane.IsHidden);         // reopened
            Assert.NotNull(pane.Owner);          // re-docked
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task ParkInactive_checkpoints_and_stops_all_hidden_agents_without_removing_them()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var launcher = new FakeLauncher();
            using var vm = await MainWindowViewModel.InitializeAsync(
                root, launcher, new FakeWatcher { WillChange = true });
            var pane = vm.Panes[0];
            for (var i = 0; i < 100 && pane.State != SessionState.Live; i++)
                await Task.Delay(10);

            vm.HideAgentCommand.Execute(pane);
            await vm.ParkInactiveCommand.ExecuteAsync(null);

            Assert.True(pane.IsHidden);
            Assert.Contains(pane, vm.InactiveAgents);
            Assert.Contains(pane, vm.Panes);
            Assert.Equal(SessionState.Dehydrated, pane.State);
            Assert.True(launcher.Spawned.Single().Disposed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
