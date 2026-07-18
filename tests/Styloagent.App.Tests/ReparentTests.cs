using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Roster drag-drop = REPARENT within a repo (operator, v2a): change an agent's owner + recompute depth,
/// guarded by the existing Core authority lint (cycle / root / worktree-owner), MaxDepth, and a confirm.
/// Illegal moves snap back with a reason; cross-repo is deferred.
/// </summary>
public class ReparentTests
{
    private static AgentPaneViewModel Pane(string prefix, string? parent, int depth, bool worktree = false)
    {
        var entry = new AgentManifestEntry(prefix, "/repo", "/repo/wt", "", "", "/ctx.md", AgentTransport.Local);
        var session = new AgentSession(entry, new FakeLauncher(), new FakeWatcher());
        var pane = new AgentPaneViewModel(session, entry, prefix.TrimEnd('-'), "#888888")
        {
            ParentPrefix = parent,
            Depth = depth,
        };
        if (worktree) pane.WorktreePath = "/repo/wt-" + prefix;
        return pane;
    }

    /// <summary>A clean single-repo fleet: overview-(root) → a-(→ a1-), b-. All same repo (no repos set).</summary>
    private static async Task<(MainWindowViewModel vm, AgentPaneViewModel overview, AgentPaneViewModel a,
        AgentPaneViewModel a1, AgentPaneViewModel b)> Fleet()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            MainWindowViewModelTests.MakeTwoAgentChannel(), new FakeLauncher(), new FakeWatcher());
        vm.Panes.Clear();
        var overview = Pane("overview-", null, 0);
        var a = Pane("a-", "overview-", 1);
        var a1 = Pane("a1-", "a-", 2);
        var b = Pane("b-", "overview-", 1);
        foreach (var p in new[] { overview, a, a1, b }) vm.Panes.Add(p);
        return (vm, overview, a, a1, b);
    }

    [Fact]
    public async Task Reparent_within_repo_moves_the_agent_and_recomputes_depth()
    {
        var (vm, _, a, a1, b) = await Fleet();

        var r = await vm.ReparentAgentAsync(b, a);   // move b- under a-

        Assert.True(r.Applied);
        Assert.Equal("a-", b.ParentPrefix);
        Assert.Equal(2, b.Depth);                    // a-.Depth(1) + 1
        Assert.Equal(2, a1.Depth);                   // a-'s existing child unchanged
    }

    [Fact]
    public async Task Reparent_recomputes_depth_for_the_whole_moved_subtree()
    {
        var (vm, overview, a, a1, b) = await Fleet();
        // Move a- (which owns a1-) under b-: a- → depth 2, a1- → depth 3.
        var r = await vm.ReparentAgentAsync(a, b);

        Assert.True(r.Applied);
        Assert.Equal("b-", a.ParentPrefix);
        Assert.Equal(2, a.Depth);
        Assert.Equal(3, a1.Depth);                   // descendant depth cascaded
    }

    [Fact]
    public async Task Reparent_onto_own_descendant_is_rejected_as_a_cycle()
    {
        var (vm, _, a, a1, _) = await Fleet();

        var r = await vm.ReparentAgentAsync(a, a1);   // a- under its own descendant a1-

        Assert.False(r.Applied);
        Assert.Contains("descendant", r.Reason);
        Assert.Equal("overview-", a.ParentPrefix);    // unchanged (snap back)
    }

    [Fact]
    public async Task Reparent_a_root_overview_is_rejected()
    {
        var (vm, overview, a, _, _) = await Fleet();

        var r = await vm.ReparentAgentAsync(overview, a);

        Assert.False(r.Applied);
        Assert.Contains("overview", r.Reason);
        Assert.Null(overview.ParentPrefix);
    }

    [Fact]
    public async Task Reparent_that_would_exceed_max_depth_is_rejected()
    {
        var (vm, _, a, a1, b) = await Fleet();
        var a1x = Pane("a1x-", "a1-", 3);             // overview(0)→a(1)→a1(2)→a1x(3); MaxDepth is 3
        vm.Panes.Add(a1x);

        // Moving a- (subtree height 2) under b-(1) → a1x lands at depth 4 > 3.
        var r = await vm.ReparentAgentAsync(a, b);

        Assert.False(r.Applied);
        Assert.Contains("max depth", r.Reason);
        Assert.Equal("overview-", a.ParentPrefix);
    }

    [Fact]
    public async Task Reparent_onto_a_worktree_holder_is_rejected_by_the_lint()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            MainWindowViewModelTests.MakeTwoAgentChannel(), new FakeLauncher(), new FakeWatcher());
        vm.Panes.Clear();
        var overview = Pane("overview-", null, 0);
        var worker = Pane("worker-", "overview-", 1, worktree: true);   // holds a worktree
        var b = Pane("b-", "overview-", 1);
        foreach (var p in new[] { overview, worker, b }) vm.Panes.Add(p);

        var r = await vm.ReparentAgentAsync(b, worker);   // worker would become an owner-with-worktree

        Assert.False(r.Applied);
        Assert.Contains("worktree", r.Reason);
        Assert.Equal("overview-", b.ParentPrefix);
    }

    [Fact]
    public async Task Reparent_cancelled_at_confirm_changes_nothing()
    {
        var (vm, _, a, _, b) = await Fleet();
        vm.ConfirmReparentAsync = _ => Task.FromResult(false);

        var r = await vm.ReparentAgentAsync(b, a);

        Assert.True(r.Cancelled);
        Assert.Equal("overview-", b.ParentPrefix);   // unchanged
        Assert.Equal(1, b.Depth);
    }
}
