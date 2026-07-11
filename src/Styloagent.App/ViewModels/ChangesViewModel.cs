using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.Core.Git;
using Styloagent.Git;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Lists the changed files in a worktree, splits them into staged / unstaged sections,
/// loads the per-file diff into <see cref="Diff"/> when a file is selected, and exposes
/// awaitable write commands (stage, unstage, commit, push, pull).
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IGitDiff _diff;
    private readonly IGitWrite _write;
    private string _worktreePath = string.Empty;

    [ObservableProperty]
    private GitChange? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _commitMessage = "";

    public ObservableCollection<GitChange> Files        { get; } = new();
    public ObservableCollection<GitChange> StagedFiles  { get; } = new();
    public ObservableCollection<GitChange> UnstagedFiles { get; } = new();

    public DiffViewModel Diff { get; } = new();

    /// <summary>True when there is at least one staged file and a non-empty commit message.</summary>
    public bool CanCommit => StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    public ChangesViewModel(IGitService git, IGitDiff diff, IGitWrite write)
    {
        _git   = git;
        _diff  = diff;
        _write = write;
    }

    /// <summary>Clears all file lists, the diff, and the commit message.</summary>
    public void Clear()
    {
        _worktreePath = string.Empty;
        SelectedFile  = null;
        CommitMessage = "";
        Files.Clear();
        StagedFiles.Clear();
        UnstagedFiles.Clear();
        Diff.File = null;
    }

    /// <summary>
    /// Fetches the worktree status and populates <see cref="Files"/>,
    /// <see cref="StagedFiles"/>, and <see cref="UnstagedFiles"/>.
    /// </summary>
    public async Task LoadAsync(string worktreePath)
    {
        _worktreePath = worktreePath;
        Files.Clear();
        StagedFiles.Clear();
        UnstagedFiles.Clear();

        var result = await _git.GetStatusAsync(worktreePath);
        if (!result.Ok || result.Value is null)
        {
            OnPropertyChanged(nameof(CanCommit));
            return;
        }

        foreach (var change in result.Value.Changes)
        {
            Files.Add(change);
            if (change.Staged)   StagedFiles.Add(change);
            if (change.Unstaged) UnstagedFiles.Add(change);
        }

        OnPropertyChanged(nameof(CanCommit));
    }

    /// <summary>
    /// Sets <see cref="SelectedFile"/> and loads the diff for that file.
    /// </summary>
    public async Task SelectFileAsync(GitChange file)
    {
        SelectedFile = file;
        var result = await _diff.GetDiffAsync(_worktreePath, file.Path, staged: false);
        Diff.File = result.Ok ? result.Value : null;
    }

    /// <summary>Stages <paramref name="change"/> then reloads the status.</summary>
    public async Task StageAsync(GitChange change)
    {
        await _write.StageAsync(_worktreePath, change.Path);
        await LoadAsync(_worktreePath);
    }

    /// <summary>Unstages <paramref name="change"/> then reloads the status.</summary>
    public async Task UnstageAsync(GitChange change)
    {
        await _write.UnstageAsync(_worktreePath, change.Path);
        await LoadAsync(_worktreePath);
    }

    /// <summary>
    /// Commits the staged files with <see cref="CommitMessage"/>. No-ops when
    /// <see cref="CanCommit"/> is false. Clears the message on success then reloads.
    /// </summary>
    public async Task CommitAsync()
    {
        if (!CanCommit) return;

        var r = await _write.CommitAsync(_worktreePath, CommitMessage);
        if (r.Ok) CommitMessage = "";
        await LoadAsync(_worktreePath);
    }

    /// <summary>Pushes the current branch then reloads.</summary>
    public async Task PushAsync()
    {
        await _write.PushAsync(_worktreePath);
        await LoadAsync(_worktreePath);
    }

    /// <summary>Pulls the current branch then reloads.</summary>
    public async Task PullAsync()
    {
        await _write.PullAsync(_worktreePath);
        await LoadAsync(_worktreePath);
    }
}
