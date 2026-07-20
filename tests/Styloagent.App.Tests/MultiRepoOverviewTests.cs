using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Phase 3: a workspace of N repos opens the primary repo's overview (existing flow) plus one overview
/// pane per additional repo, each coloured by its repo hue, on the shared bus. These drive
/// <see cref="MainWindowViewModel.InitializeAsync"/> with a fake launcher so no real claude is spawned.
/// </summary>
public class MultiRepoOverviewTests
{
    [Fact]
    public async Task Extra_overviews_each_add_one_pane_coloured_by_its_repo()
    {
        // Two independent channels seeded identically → a clean before/after pane-count baseline.
        var baseChannel = MainWindowViewModelTests.MakeTwoAgentChannel();
        var wsChannel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var baseline = await MainWindowViewModel.InitializeAsync(
                baseChannel, new FakeLauncher(), new FakeWatcher());

            var ws = WorkspaceConfig.For("/ws", "mono", new[]
            {
                Path.Combine("/ws", "primary"),
                Path.Combine("/ws", "beta"),
                Path.Combine("/ws", "gamma"),
            });
            var overviews = ws.RepoOverviews();
            var primaryColor = overviews[0].ColorHex;
            var extras = overviews.Skip(1).ToList();            // beta-, gamma-
            Assert.Equal(2, extras.Count);

            var vm = await MainWindowViewModel.InitializeAsync(
                wsChannel, new FakeLauncher(), new FakeWatcher(),
                overviewColorHex: primaryColor, extraOverviews: extras);

            // The primary overview is coloured by its own repo hue (repo 0).
            Assert.Equal(primaryColor, vm.Pane!.BorderColorHex);

            // Exactly one extra pane per additional repo.
            Assert.Equal(baseline.Panes.Count + extras.Count, vm.Panes.Count);

            foreach (var ov in extras)
            {
                var pane = vm.Panes.FirstOrDefault(p => p.Prefix == ov.Prefix);
                Assert.NotNull(pane);
                Assert.Equal(ov.ColorHex, pane!.BorderColorHex);   // coloured by repo hue
            }

            // Distinct repos → distinct hues.
            Assert.NotEqual(extras[0].ColorHex, extras[1].ColorHex);
        }
        finally
        {
            if (Directory.Exists(baseChannel)) Directory.Delete(baseChannel, recursive: true);
            if (Directory.Exists(wsChannel)) Directory.Delete(wsChannel, recursive: true);
        }
    }

    [Fact]
    public async Task ListRepos_reflects_the_workspace_overviews()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            var ws = WorkspaceConfig.For("/ws", "mono", new[]
            {
                Path.Combine("/ws", "Styloagent"),
                Path.Combine("/ws", "lucidRESUME"),
            });
            vm.SetReposFromOverviews(ws.RepoOverviews());

            var repos = vm.BuildRepoList();

            Assert.Equal(2, repos.Count);
            Assert.Equal("overview-", repos[0].Prefix);
            Assert.True(repos[0].Primary);
            Assert.Equal("Styloagent", repos[0].Name);
            Assert.Equal("lucidresume-", repos[1].Prefix);
            Assert.False(repos[1].Primary);
            Assert.Equal("lucidRESUME", repos[1].Name);
            Assert.NotEqual(repos[0].ColorHex, repos[1].ColorHex);
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }

    [Fact]
    public async Task FleetStatus_tags_each_agent_with_its_repo()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var ws = WorkspaceConfig.For("/ws", "mono", new[]
            {
                Path.Combine("/ws", "primary"),
                Path.Combine("/ws", "beta"),
            });
            var vm = await MainWindowViewModel.InitializeAsync(
                channel, new FakeLauncher(), new FakeWatcher(),
                extraOverviews: ws.RepoOverviews().Skip(1).ToList());
            vm.SetReposFromOverviews(ws.RepoOverviews());

            var beta = vm.BuildFleetStatus().Agents.FirstOrDefault(a => a.Prefix == "beta-");
            Assert.NotNull(beta);
            Assert.Equal("beta", beta!.Repo);
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }

    [Fact]
    public async Task Dilution_guard_nudges_once_when_context_fills_then_re_arms()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            var pane = vm.Panes[0];
            Assert.Equal(SessionState.Live, pane.State);   // FakeLauncher + zero inject-delays spawn synchronously

            pane.ContextFraction = 0.92;
            vm.CheckContextDilution();
            vm.CheckContextDilution();                     // must not double-nudge

            Assert.Equal(1, vm.Timeline.Entries.Count(e => e.Description.Contains("dehydrating")));

            // Drops well below the line → re-arms, so a later fill nudges again.
            pane.ContextFraction = 0.5;
            vm.CheckContextDilution();
            pane.ContextFraction = 0.92;
            vm.CheckContextDilution();
            Assert.Equal(2, vm.Timeline.Entries.Count(e => e.Description.Contains("dehydrating")));
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }

    [Fact]
    public async Task No_extra_overviews_leaves_the_single_repo_roster_unchanged()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var withNull = await MainWindowViewModel.InitializeAsync(
                channel, new FakeLauncher(), new FakeWatcher(), extraOverviews: null);
            var withEmpty = await MainWindowViewModel.InitializeAsync(
                channel, new FakeLauncher(), new FakeWatcher(), extraOverviews: Array.Empty<RepoOverview>());

            // A null or empty extra-overview list is the released single-repo path: same roster.
            Assert.Equal(withNull.Panes.Count, withEmpty.Panes.Count);
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }
}
