using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// BUG 4 (federated pane→PTY wiring): a child spawned by a federated repo's overview must carry that
/// repo's tag, so <c>ResolvePtyForRepo</c> / <c>SnapshotLiveAgentsForRepo</c> can route delivery to it.
/// Previously only the federated OVERVIEW was tagged, so its spawned children were invisible to their
/// own instance's coordinator. (The terminal-DISPLAY blank is a separate, session-domain concern.)
/// </summary>
public class FederatedPaneRepoTagTests
{
    [Fact]
    public async Task Spawned_federated_child_inherits_its_parents_repo_tag()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            vm.SetReposFromOverviews(new[] { new RepoOverview(
                "overview-", "/ws/styloagent", "/ws/styloagent/.styloagent/system-prompt.md", 0, "#4CDB6E", true) });

            // Live-open a federated repo: its overview pane is tagged with the repo root (:1097).
            var second = new RepoOverview("styloissues-", "/ws/styloissues",
                "/ws/styloissues/.styloagent/system-prompt.md", 1, "#C77DFF", false);
            vm.AddWorkspaceRepo(second);
            vm.AddRepoOverview(second);
            Assert.Equal("/ws/styloissues", vm.RepoRootForPaneForTest("styloissues-"));

            // That overview spawns a child — it must inherit the federated repo tag (the fix).
            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                "styloissues-", "contracts-", "contracts", ".", "read your mission", false));
            Assert.True(outcome.Spawned, outcome.Message);

            Assert.Equal("/ws/styloissues", vm.RepoRootForPaneForTest("contracts-"));   // routes to its own instance

            // A primary-fleet agent stays untagged (the default → the primary coordinator handles it).
            Assert.Null(vm.RepoRootForPaneForTest("overview-"));
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }
}
