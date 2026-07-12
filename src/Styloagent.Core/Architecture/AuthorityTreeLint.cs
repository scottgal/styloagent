namespace Styloagent.Core.Architecture;

/// <summary>One node in the C4 mutation-authority graph: an agent, its owner (parent authority), and
/// whether it holds a worktree. A root authority has no parent.</summary>
public sealed record AuthorityNode(string Prefix, string? ParentPrefix, bool HasWorktree);

/// <summary>A broken authority-graph invariant. <see cref="Kind"/> is a stable machine tag; <see cref="Detail"/> explains it.</summary>
public sealed record AuthorityViolation(string Kind, string Prefix, string Detail);

/// <summary>
/// Lints the C4 mutation-authority graph. The org chart can split and merge underneath you as overviews
/// spawn sub-overviews, so the one thing that must hold is that authority stays a <b>tree</b>: exactly one
/// root, one owner per node, acyclic — and (the structural source of an overview's neutrality) no node that
/// exercises authority over others may hold a worktree. An overseer with skin in the file-level game is not
/// a credible arbiter. Pure and deterministic; the checkable version of "the cool head stays coherent."
/// </summary>
public static class AuthorityTreeLint
{
    /// <summary>Returns every violation of the authority-tree invariants (empty ⇒ a coherent authority tree).</summary>
    public static IReadOnlyList<AuthorityViolation> Check(IReadOnlyList<AuthorityNode> nodes)
    {
        var violations = new List<AuthorityViolation>();

        var byPrefix = new Dictionary<string, AuthorityNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
            if (!byPrefix.TryAdd(n.Prefix, n))
                violations.Add(new("duplicate-node", n.Prefix, "two agents share this prefix — ownership is ambiguous"));

        // Children per node, and missing-parent checks.
        var childCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (string.IsNullOrEmpty(n.ParentPrefix)) continue;
            if (!byPrefix.ContainsKey(n.ParentPrefix))
                violations.Add(new("missing-parent", n.Prefix, $"owner '{n.ParentPrefix}' is not in the fleet"));
            else
                childCount[n.ParentPrefix] = childCount.GetValueOrDefault(n.ParentPrefix) + 1;
        }

        // Exactly one root (a present, single final arbiter).
        var roots = nodes.Where(n => string.IsNullOrEmpty(n.ParentPrefix)).Select(n => n.Prefix).Distinct(StringComparer.Ordinal).ToList();
        if (roots.Count == 0 && nodes.Count > 0)
            violations.Add(new("no-root", "", "no root authority — every node has an owner, so the graph cannot be a tree"));
        foreach (var extra in roots.Skip(1))
            violations.Add(new("multiple-roots", extra, "more than one root authority; a single overview must be the root arbiter"));

        // Acyclic: walk each node's owner chain; a revisit is a cycle.
        foreach (var start in nodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = start;
            while (cur is not null && !string.IsNullOrEmpty(cur.ParentPrefix))
            {
                if (!seen.Add(cur.Prefix))
                {
                    violations.Add(new("cycle", start.Prefix, "authority chain forms a cycle — no final arbiter"));
                    break;
                }
                byPrefix.TryGetValue(cur.ParentPrefix, out cur);
            }
        }

        // No owner (a node with children) may hold a worktree — overseer, not worker.
        foreach (var n in nodes)
            if (n.HasWorktree && childCount.GetValueOrDefault(n.Prefix) > 0)
                violations.Add(new("owner-has-worktree", n.Prefix,
                    "holds a worktree yet has authority over child agents — an overseer with skin in the game cannot arbitrate"));

        // One violation per (kind, prefix) — a cycle shared by N nodes shouldn't report N times for the same tag.
        return violations
            .GroupBy(v => (v.Kind, v.Prefix))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>True when the authority graph is a coherent tree (no violations).</summary>
    public static bool IsTree(IReadOnlyList<AuthorityNode> nodes) => Check(nodes).Count == 0;
}
