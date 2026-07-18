using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Operator: when agents from multiple repos are docked together as tabs, each tab needs a repo cue. The
/// agent tab header carries a repo-colour band (the repo overview's hue, matching the roster grouping),
/// shown only in a multi-repo workspace. Covers the band DATA (colour per repo, absent single-repo); the
/// rendered stripe is a headless render check in UITests.
/// </summary>
public class TabRepoBandTests
{
    private static AgentPaneViewModel Pane(string prefix, string? parent, int depth)
    {
        var entry = new AgentManifestEntry(prefix, "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
        var session = new AgentSession(entry, new FakeLauncher(), new FakeWatcher());
        return new AgentPaneViewModel(session, entry, prefix.TrimEnd('-'), "#888888")
        {
            ParentPrefix = parent,
            Depth = depth,
        };
    }

    private static RepoOverview Repo(string prefix, string name, int index, string color, bool primary)
        => new(prefix, "/ws/" + name, $"/ws/{name}/.styloagent/system-prompt.md", index, color, primary);

    [Fact]
    public async Task Agent_tabs_get_a_per_repo_band_colour_in_a_multi_repo_workspace()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            vm.SetReposFromOverviews(new[]
            {
                Repo("overview-", "Styloagent", 0, "#4CDB6E", primary: true),
                Repo("beta-", "beta", 1, "#C77DFF", primary: false),
            });

            vm.Panes.Clear();
            var o = Pane("overview-", null, 0);          // Styloagent root
            var a = Pane("a-", "overview-", 1);          // Styloagent child
            var beta = Pane("beta-", null, 0);           // beta root
            var b = Pane("b-", "beta-", 1);              // beta child
            foreach (var p in new[] { o, a, beta, b }) vm.Panes.Add(p);   // each Add rebuilds the roster + bands

            Assert.False(string.IsNullOrEmpty(o.RepoBandColorHex));       // shown in a multi-repo workspace
            Assert.Equal(o.RepoBandColorHex, a.RepoBandColorHex);         // same repo → same band
            Assert.NotEqual(o.RepoBandColorHex, b.RepoBandColorHex);      // different repo → different band
            Assert.Equal(beta.RepoBandColorHex, b.RepoBandColorHex);
            Assert.Contains("Styloagent", o.RepoBandTooltip);
            Assert.Contains("beta", b.RepoBandTooltip);
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }

    [Fact]
    public async Task Single_repo_workspace_shows_no_tab_repo_band()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());
            vm.SetReposFromOverviews(new[] { Repo("overview-", "Styloagent", 0, "#4CDB6E", primary: true) });

            vm.Panes.Clear();
            var o = Pane("overview-", null, 0);
            var a = Pane("a-", "overview-", 1);
            foreach (var p in new[] { o, a }) vm.Panes.Add(p);

            Assert.True(string.IsNullOrEmpty(o.RepoBandColorHex));   // no band in a single-repo workspace
            Assert.True(string.IsNullOrEmpty(a.RepoBandColorHex));
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }
}
