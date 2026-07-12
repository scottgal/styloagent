using Styloagent.Core.Architecture;

namespace Styloagent.Core.Tests;

/// <summary>
/// The C4 mutation-authority graph must be a tree: one root, one owner per node, acyclic, and no owner
/// holds a worktree (overseer, not worker). These are the guardrails that keep the org chart coherent
/// as overviews split and merge.
/// </summary>
public class AuthorityTreeLintTests
{
    private static AuthorityNode Overseer(string prefix, string? parent = null) => new(prefix, parent, HasWorktree: false);
    private static AuthorityNode Worker(string prefix, string parent) => new(prefix, parent, HasWorktree: true);

    [Fact]
    public void A_root_overview_with_worker_children_is_a_valid_tree()
    {
        var nodes = new[]
        {
            Overseer("overview-"),
            Worker("foss-", "overview-"),
            Worker("deploy-", "overview-"),
        };
        Assert.True(AuthorityTreeLint.IsTree(nodes));
        Assert.Empty(AuthorityTreeLint.Check(nodes));
    }

    [Fact]
    public void A_split_overview_subtree_is_still_a_tree()
    {
        // overview- → overview.extraction- (a sub-overseer, no worktree) → its workers.
        var nodes = new[]
        {
            Overseer("overview-"),
            Overseer("overview.extraction-", "overview-"),
            Worker("hotpath-", "overview.extraction-"),
        };
        Assert.True(AuthorityTreeLint.IsTree(nodes));
    }

    [Fact]
    public void An_owner_holding_a_worktree_is_flagged()
    {
        // foss- has a child yet holds a worktree — an overseer with skin in the game.
        var nodes = new[]
        {
            Overseer("overview-"),
            new AuthorityNode("foss-", "overview-", HasWorktree: true),
            Worker("sub-", "foss-"),
        };
        var v = AuthorityTreeLint.Check(nodes);
        Assert.Contains(v, x => x.Kind == "owner-has-worktree" && x.Prefix == "foss-");
    }

    [Fact]
    public void A_leaf_worker_holding_a_worktree_is_fine()
    {
        var nodes = new[] { Overseer("overview-"), Worker("foss-", "overview-") };
        Assert.DoesNotContain(AuthorityTreeLint.Check(nodes), x => x.Kind == "owner-has-worktree");
    }

    [Fact]
    public void Two_roots_are_flagged()
    {
        var nodes = new[] { Overseer("overview-"), Overseer("rogue-") };
        Assert.Contains(AuthorityTreeLint.Check(nodes), x => x.Kind == "multiple-roots" && x.Prefix == "rogue-");
    }

    [Fact]
    public void A_missing_owner_is_flagged()
    {
        var nodes = new[] { Overseer("overview-"), Worker("foss-", "ghost-") };
        Assert.Contains(AuthorityTreeLint.Check(nodes), x => x.Kind == "missing-parent" && x.Prefix == "foss-");
    }

    [Fact]
    public void A_cycle_is_flagged_and_reported_once_per_node()
    {
        // a- owns b-, b- owns a- : a cycle with no root.
        var nodes = new[]
        {
            new AuthorityNode("a-", "b-", false),
            new AuthorityNode("b-", "a-", false),
        };
        var v = AuthorityTreeLint.Check(nodes);
        Assert.Contains(v, x => x.Kind == "cycle");
        Assert.Contains(v, x => x.Kind == "no-root");
        Assert.Equal(v.Count, v.Select(x => (x.Kind, x.Prefix)).Distinct().Count());  // deduped
    }

    [Fact]
    public void Duplicate_prefixes_are_flagged()
    {
        var nodes = new[]
        {
            Overseer("overview-"),
            Worker("foss-", "overview-"),
            Worker("foss-", "overview-"),
        };
        Assert.Contains(AuthorityTreeLint.Check(nodes), x => x.Kind == "duplicate-node" && x.Prefix == "foss-");
    }

    [Fact]
    public void An_empty_fleet_has_no_violations()
        => Assert.Empty(AuthorityTreeLint.Check(System.Array.Empty<AuthorityNode>()));
}
