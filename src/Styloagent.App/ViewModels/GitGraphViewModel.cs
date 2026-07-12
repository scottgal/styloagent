using System.Collections.ObjectModel;
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

    /// <summary>One row of history text, aligned (by <see cref="CommitGraphLayout.RowHeight"/>) with
    /// the graph dots drawn beside it.</summary>
    public sealed record CommitRow(string ShortSha, string Subject, string Author, string TimeText);

    [ObservableProperty]
    private CommitGraph? _graph;

    [ObservableProperty]
    private CommitGraphLayout? _layout;

    [ObservableProperty]
    private int _commitCount;

    /// <summary>The commit text rows shown beside the graph (subject/author/date). The graph control
    /// only draws lines and dots — without this the history looks like a graph with no text.</summary>
    public ObservableCollection<CommitRow> Commits { get; } = new();

    public GitGraphViewModel(IGitLog log) => _log = log;

    /// <summary>Blanks the graph (no worktree selected).</summary>
    public void Clear()
    {
        Graph = null;
        Layout = null;
        CommitCount = 0;
        Commits.Clear();
    }

    public async Task LoadAsync(string worktreePath)
    {
        var result = await _log.GetCommitsAsync(worktreePath);
        if (!result.Ok || result.Value is null || result.Value.Count == 0)
        {
            Clear();
            return;
        }
        var commits = new List<Commit>(result.Value);
        CommitGraph.SetDefaultPens();
        Graph = CommitGraph.Generate(commits, recalculateMergeState: false,
            firstParentOnlyEnabled: false, CommitGraphHighlighting.All, new HashSet<string>());
        Layout = new CommitGraphLayout(StartY: 0, ClipWidth: 40, RowHeight: 24);
        CommitCount = commits.Count;

        Commits.Clear();
        foreach (var c in commits)
        {
            var sha = c.SHA.Length >= 7 ? c.SHA[..7] : c.SHA;
            var when = DateTimeOffset.FromUnixTimeSeconds((long)c.CommitterTime).ToLocalTime().ToString("yyyy-MM-dd");
            Commits.Add(new CommitRow(sha, c.Subject, c.Author.Name, when));
        }
    }
}
