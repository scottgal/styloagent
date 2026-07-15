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
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);

            var pane = vm.Panes[0];
            Assert.False(pane.IsHidden);
            Assert.NotEqual(SessionState.Dehydrated, pane.State);

            vm.HideAgentCommand.Execute(pane);

            Assert.True(pane.IsHidden);                              // marked hidden (off-screen)
            Assert.Contains(pane, vm.Panes);                        // still in the roster — not torn down
            Assert.NotEqual(SessionState.Dehydrated, pane.State);   // still RUNNING — hide != dehydrate

            vm.ShowAgentCommand.Execute(pane);

            Assert.False(pane.IsHidden);                            // restored, no rehydrate needed
            Assert.Contains(pane, vm.Panes);
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
            var vm = await MainWindowViewModel.InitializeAsync(
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
}
