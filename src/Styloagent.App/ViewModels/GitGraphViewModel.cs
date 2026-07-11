using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Loads a worktree's recent history via <see cref="IGitLog"/> and builds the vendored
/// <see cref="CommitGraph"/> the graph control renders. Read-only (Plan 2a).
/// </summary>
public sealed partial class GitGraphViewModel : ObservableObject
{
    private readonly IGitLog _log;

    [ObservableProperty]
    private CommitGraph? _graph;

    [ObservableProperty]
    private int _commitCount;

    public GitGraphViewModel(IGitLog log) => _log = log;

    public async Task LoadAsync(string worktreePath)
    {
        var result = await _log.GetCommitsAsync(worktreePath);
        if (!result.Ok || result.Value is null || result.Value.Count == 0)
        {
            Graph = null;
            CommitCount = 0;
            return;
        }
        var commits = new List<Commit>(result.Value);
        CommitGraph.SetDefaultPens();
        Graph = CommitGraph.Generate(commits, recalculateMergeState: false,
            firstParentOnlyEnabled: false, CommitGraphHighlighting.All, new HashSet<string>());
        CommitCount = commits.Count;
    }
}
