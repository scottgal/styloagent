using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Issues;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model for the Issues panel. Reads the file-drop issues under a project's
/// <c>.styloagent/issues/</c> and exposes them newest-first for the roster to triage. Agents file
/// issues over MCP (<c>report_issue</c>); a future GitHub feed will land here too via a triage agent.
/// </summary>
public sealed partial class IssuesViewModel : ObservableObject
{
    private readonly string _issuesDir;

    public ObservableCollection<Issue> Issues { get; } = new();

    /// <summary>Count of open issues — drives the tab badge.</summary>
    public int OpenCount => Issues.Count(i => i.Status == "open");

    /// <summary>Empty-state visibility for the view.</summary>
    public bool HasIssues => Issues.Count > 0;

    public IssuesViewModel(string issuesDir)
    {
        _issuesDir = issuesDir;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Issues.Clear();
        foreach (var issue in IssueStore.Read(_issuesDir))
            Issues.Add(issue);
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(HasIssues));
    }
}
