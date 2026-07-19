using Styloagent.App.ViewModels;
using Styloagent.Core.Workspace;

namespace Styloagent.App.Tests;

public class GitSidebarRepositoryIdentityTests
{
    [Fact]
    public async Task Multi_repo_sidebar_identity_tracks_the_selected_pane_and_is_hidden_for_one_repo()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            var primary = new RepoOverview("overview-", "/ws/primary",
                "/ws/primary/.styloagent/system-prompt.md", 0, "#4CDB6E", true);
            var beta = new RepoOverview("beta-", "/ws/beta",
                "/ws/beta/.styloagent/system-prompt.md", 1, "#C77DFF", false);

            vm.SetReposFromOverviews(new[] { primary, beta });
            vm.AddRepoOverview(beta);

            Assert.Equal("primary", vm.GitSidebarRepositoryName);

            vm.SelectedPane = vm.Panes.Single(p => p.Prefix == "beta-");

            Assert.Equal("beta", vm.GitSidebarRepositoryName);

            vm.SetReposFromOverviews(new[] { primary });

            Assert.Equal("", vm.GitSidebarRepositoryName);
        }
        finally
        {
            if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true);
        }
    }
}
