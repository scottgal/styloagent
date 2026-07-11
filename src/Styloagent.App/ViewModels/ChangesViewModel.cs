using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.Core.Git;
using Styloagent.Git;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Lists the changed files in a worktree and loads the per-file diff into
/// <see cref="Diff"/> when a file is selected via <see cref="SelectFileAsync"/>.
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IGitDiff _diff;
    private string _worktreePath = string.Empty;

    [ObservableProperty]
    private GitChange? _selectedFile;

    public ObservableCollection<GitChange> Files { get; } = new();

    public DiffViewModel Diff { get; } = new();

    public ChangesViewModel(IGitService git, IGitDiff diff)
    {
        _git = git;
        _diff = diff;
    }

    /// <summary>Clears the file list and diff (no worktree selected).</summary>
    public void Clear()
    {
        _worktreePath = string.Empty;
        SelectedFile = null;
        Files.Clear();
        Diff.File = null;
    }

    /// <summary>
    /// Fetches the worktree status and populates <see cref="Files"/>.
    /// </summary>
    public async Task LoadAsync(string worktreePath)
    {
        _worktreePath = worktreePath;
        Files.Clear();

        var result = await _git.GetStatusAsync(worktreePath);
        if (!result.Ok || result.Value is null)
            return;

        foreach (var change in result.Value.Changes)
            Files.Add(change);
    }

    /// <summary>
    /// Sets <see cref="SelectedFile"/> and awaits the diff load for that file.
    /// The view binds its selection-changed handler to this method (via a command);
    /// the test calls it directly for deterministic assertions.
    /// </summary>
    public async Task SelectFileAsync(GitChange file)
    {
        SelectedFile = file;
        var result = await _diff.GetDiffAsync(_worktreePath, file.Path, staged: false);
        Diff.File = result.Ok ? result.Value : null;
    }
}
