using Styloagent.Core.Mcp;

namespace Styloagent.App.ViewModels;

/// <summary>
/// One repo's slice of the Agents roster: a header (shown only in a multi-repo workspace) plus that
/// repo's agents in tree order — its own overview first, then its children. Grouping by repo is what
/// keeps every repo's fleet rooted at ITS OWN overview; the flat, depth-indented list conflated repos
/// so cross-repo children looked parented under the wrong overview (BUG 3).
/// </summary>
public sealed class RosterRepoGroup
{
    /// <summary>The repo's display name (its folder name); empty in a single-repo workspace with no repo set.</summary>
    public string RepoName { get; init; } = "";

    /// <summary>The repo's identity hue (its overview's colour) — the header accent.</summary>
    public string ColorHex { get; init; } = "#8888AA";

    /// <summary>Show the repo header — only when the workspace actually spans more than one repo, so a
    /// single-repo roster renders exactly as it did before (no redundant header).</summary>
    public bool ShowHeader { get; init; }

    /// <summary>The repo's agents, in insertion (tree) order: overview first, then its children.</summary>
    public IReadOnlyList<AgentPaneViewModel> Agents { get; init; } = System.Array.Empty<AgentPaneViewModel>();
}

/// <summary>
/// Groups the flat pane roster into per-repo slices so each repo's fleet roots at ITS OWN overview
/// (BUG 3). Pure and side-effect-free so it is unit-testable with fabricated panes + repos — feed it
/// flat data, assert the grouping; no cross-repo owner resolution ever happens.
/// </summary>
public static class RosterGrouping
{
    /// <summary>
    /// Bucket <paramref name="panes"/> by the repo <paramref name="repoNameOf"/> attributes each to,
    /// preserving each repo's first-appearance order and within-repo insertion order (panes are added
    /// overview-first then children, so insertion order is already tree order). Groups are ordered by
    /// each repo's <see cref="RepoInfo.Index"/> (primary/anchor first), unknown repos last by appearance.
    /// The header is shown only when more than one repo is known.
    /// </summary>
    public static IReadOnlyList<RosterRepoGroup> Build(
        IReadOnlyList<AgentPaneViewModel> panes,
        IReadOnlyList<RepoInfo> repos,
        Func<AgentPaneViewModel, string> repoNameOf)
    {
        var byRepo = new Dictionary<string, List<AgentPaneViewModel>>();
        var appearance = new List<string>();
        foreach (var p in panes)
        {
            string name = repoNameOf(p) ?? "";
            if (!byRepo.TryGetValue(name, out var list))
            {
                list = new List<AgentPaneViewModel>();
                byRepo[name] = list;
                appearance.Add(name);
            }
            list.Add(p);
        }

        bool multiRepo = repos.Count > 1;
        int RepoIndex(string name)
        {
            var hit = repos.FirstOrDefault(r => r.Name == name);
            return hit is not null ? hit.Index : int.MaxValue;   // unknown repos sort last, by appearance
        }

        return appearance
            .OrderBy(RepoIndex)
            .ThenBy(appearance.IndexOf)
            .Select(name => new RosterRepoGroup
            {
                RepoName   = name,
                ColorHex   = repos.FirstOrDefault(r => r.Name == name)?.ColorHex ?? "#8888AA",
                ShowHeader = multiRepo && name.Length > 0,
                Agents     = byRepo[name],
            })
            .ToList();
    }
}
