using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// BUG 3: the Agents roster must group agents by repo so each repo's fleet roots at ITS OWN overview
/// (a flat, depth-indented list made cross-repo children look parented under the wrong repo). Covers
/// the pure grouping (<see cref="RosterGrouping"/>) and the live-open attribution fix end-to-end.
/// </summary>
public class RosterGroupingTests
{
    private static AgentPaneViewModel Pane(string prefix)
    {
        var entry = new AgentManifestEntry(prefix, "/repo", "/repo", "", "", "", AgentTransport.Local);
        var session = new AgentSession(entry, new FakeLauncher(), new FakeWatcher());
        return new AgentPaneViewModel(session, entry, prefix.TrimEnd('-'), "#888888");
    }

    [Fact]
    public void Build_groups_by_repo_without_crossing_and_orders_primary_first()
    {
        var repos = new[]
        {
            new RepoInfo("primary", "/p", 0, "overview-", "#4CDB6E", true),
            new RepoInfo("beta", "/b", 1, "beta-", "#33AA88", false),
        };
        var panes = new[]
        {
            Pane("overview-"), Pane("session-"),            // primary
            Pane("beta-"), Pane("bcore-"), Pane("bweb-"),   // beta
        };
        var repoOf = new Dictionary<string, string>
        {
            ["overview-"] = "primary", ["session-"] = "primary",
            ["beta-"] = "beta", ["bcore-"] = "beta", ["bweb-"] = "beta",
        };

        var groups = RosterGrouping.Build(panes, repos, p => repoOf[p.Prefix]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("primary", groups[0].RepoName);   // primary (repo index 0) first
        Assert.Equal("beta", groups[1].RepoName);
        Assert.All(groups, g => Assert.True(g.ShowHeader));   // multi-repo → repo attribution shown

        // No cross-repo leakage: each group holds ONLY its own repo's agents.
        Assert.All(groups[0].Agents, a => Assert.Equal("primary", repoOf[a.Prefix]));
        Assert.All(groups[1].Agents, a => Assert.Equal("beta", repoOf[a.Prefix]));

        // The overview roots each group; the children are under it (insertion/tree order preserved).
        Assert.Equal("overview-", groups[0].Agents[0].Prefix);
        Assert.Equal("beta-", groups[1].Agents[0].Prefix);
        Assert.Contains(groups[1].Agents, a => a.Prefix == "bcore-");
        Assert.Contains(groups[1].Agents, a => a.Prefix == "bweb-");
    }

    [Fact]
    public void Build_single_repo_shows_no_header()
    {
        var repos = new[] { new RepoInfo("solo", "/s", 0, "overview-", "#4CDB6E", true) };
        var panes = new[] { Pane("overview-"), Pane("session-") };

        var groups = RosterGrouping.Build(panes, repos, _ => "solo");

        Assert.Single(groups);
        Assert.False(groups[0].ShowHeader);   // single repo → no redundant header (renders like before)
        Assert.Equal(2, groups[0].Agents.Count);
    }

    // The live-open regression (the operator's actual scenario): a repo opened mid-session registers with
    // the workspace, so its overview spawns children that nest under IT — never mis-parented to the primary.
    [Fact]
    public async Task LiveOpenedRepo_child_nests_under_its_own_repo_not_the_primary()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());

            // Startup: the workspace knows only the primary repo (overview- anchors it).
            var primary = new RepoOverview("overview-", "/ws/styloagent",
                "/ws/styloagent/.styloagent/system-prompt.md", 0, "#4CDB6E", true);
            vm.SetReposFromOverviews(new[] { primary });

            // Live-open a second repo (the federated gesture): register it, then add its overview pane —
            // the exact order OpenFederatedInstanceAsync uses.
            var second = new RepoOverview("styloissues-", "/ws/styloissues",
                "/ws/styloissues/.styloagent/system-prompt.md", 1, "#C77DFF", false);
            vm.AddWorkspaceRepo(second);
            vm.AddRepoOverview(second);

            // That repo's overview spawns a child — the operator's actual scenario.
            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                "styloissues-", "contracts-", "contracts", ".", "read your mission", false));
            Assert.True(outcome.Spawned, outcome.Message);

            // The child nests under styloissues (repo-scoped attribution + grouping)...
            var styloissues = vm.RosterGroups.First(g => g.RepoName == "styloissues");
            Assert.True(styloissues.ShowHeader);                                   // attribution shown
            Assert.Equal("styloissues-", styloissues.Agents[0].Prefix);           // its overview roots the group
            Assert.Contains(styloissues.Agents, a => a.Prefix == "contracts-");   // the child is UNDER it

            // ...and is NOT mis-parented into the primary repo (the bug).
            var primaryGroup = vm.RosterGroups.First(g => g.RepoName == "styloagent");
            Assert.DoesNotContain(primaryGroup.Agents, a => a.Prefix == "contracts-");
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }
}
