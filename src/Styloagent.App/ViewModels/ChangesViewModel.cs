using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Git;
using Styloagent.Git;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Lists the changed files in a worktree, splits them into staged / unstaged sections,
/// loads the per-file diff into <see cref="Diff"/> when a file is selected, and exposes
/// awaitable write commands (stage, unstage, commit, push, pull, switch branch, create branch).
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IGitDiff _diff;
    private readonly IGitWrite _write;
    private readonly IGitBranch _branch;
    private readonly IGitStash _stash;
    private string _worktreePath = string.Empty;

    [ObservableProperty]
    private GitChange? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _commitMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWriteError))]
    private string? _writeError;

    [ObservableProperty]
    private string? _currentBranch;

    [ObservableProperty]
    private GitBranch? _selectedBranch;

    private bool _loadingBranches;

    [ObservableProperty]
    private string _newBranchName = "";

    public bool HasWriteError => !string.IsNullOrEmpty(WriteError);

    public ObservableCollection<GitChange> Files        { get; } = new();
    public ObservableCollection<GitChange> StagedFiles  { get; } = new();
    public ObservableCollection<GitChange> UnstagedFiles { get; } = new();
    public ObservableCollection<GitBranch> Branches     { get; } = new();
    public ObservableCollection<string>    Stashes      { get; } = new();

    public DiffViewModel Diff { get; } = new();

    /// <summary>True when there is at least one staged file and a non-empty commit message.</summary>
    public bool CanCommit => StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    public ChangesViewModel(IGitService git, IGitDiff diff, IGitWrite write, IGitBranch branch, IGitStash stash)
    {
        _git    = git;
        _diff   = diff;
        _write  = write;
        _branch = branch;
        _stash  = stash;
    }

    private void Report(GitResult r) => WriteError = r.Ok ? null : r.Error;

    /// <summary>Clears all file lists, the diff, the commit message, branch state, and any write error.</summary>
    public void Clear()
    {
        _worktreePath = string.Empty;
        SelectedFile  = null;
        CommitMessage = "";
        WriteError    = null;
        CurrentBranch = null;
        NewBranchName = "";
        _loadingBranches = true;
        try
        {
            SelectedBranch = null;
            Branches.Clear();
        }
        finally { _loadingBranches = false; }
        Files.Clear();
        StagedFiles.Clear();
        UnstagedFiles.Clear();
        Stashes.Clear();
        Diff.File = null;
    }

    /// <summary>
    /// Fetches the worktree status and populates <see cref="Files"/>,
    /// <see cref="StagedFiles"/>, and <see cref="UnstagedFiles"/>.
    /// Also refreshes <see cref="Branches"/> and <see cref="CurrentBranch"/>.
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
            await LoadBranchesAsync();
            await LoadStashesAsync();
            return;
        }

        foreach (var change in result.Value.Changes)
        {
            Files.Add(change);
            if (change.Staged)   StagedFiles.Add(change);
            if (change.Unstaged) UnstagedFiles.Add(change);
        }

        OnPropertyChanged(nameof(CanCommit));
        await LoadBranchesAsync();
        await LoadStashesAsync();
    }

    /// <summary>Fetches the stash list and repopulates <see cref="Stashes"/>.</summary>
    private async Task LoadStashesAsync()
    {
        var r = await _stash.ListStashesAsync(_worktreePath);
        if (!r.Ok || r.Value is null) return;

        Stashes.Clear();
        foreach (var entry in r.Value)
            Stashes.Add(entry);
    }

    /// <summary>Fetches the branch list and updates <see cref="Branches"/>, <see cref="CurrentBranch"/>, and <see cref="SelectedBranch"/>.</summary>
    private async Task LoadBranchesAsync()
    {
        var r = await _branch.ListBranchesAsync(_worktreePath);
        if (!r.Ok || r.Value is null) return;

        _loadingBranches = true;
        try
        {
            Branches.Clear();
            foreach (var b in r.Value)
                Branches.Add(b);

            CurrentBranch  = Branches.FirstOrDefault(b => b.IsCurrent)?.Name;
            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent);
        }
        finally { _loadingBranches = false; }
    }

    /// <summary>Fires a branch switch when the user selects a non-current branch; no-ops during data load.</summary>
    partial void OnSelectedBranchChanged(GitBranch? value)
    {
        if (_loadingBranches || value is null || value.IsCurrent) return;
        _ = SwitchAsync(value);
    }

    /// <summary>Switches to <paramref name="b"/> then reloads.</summary>
    [RelayCommand]
    public async Task SwitchAsync(GitBranch b)
    {
        Report(await _branch.SwitchBranchAsync(_worktreePath, b.Name));
        await LoadAsync(_worktreePath);
    }

    /// <summary>Creates a new branch from <see cref="NewBranchName"/> then reloads. No-ops on whitespace.</summary>
    [RelayCommand]
    public async Task CreateBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBranchName)) return;
        var r = await _branch.CreateBranchAsync(_worktreePath, NewBranchName.Trim());
        Report(r);
        if (r.Ok) NewBranchName = "";
        await LoadAsync(_worktreePath);
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
    [RelayCommand]
    public async Task StageAsync(GitChange change)
    {
        Report(await _write.StageAsync(_worktreePath, change.Path));
        await LoadAsync(_worktreePath);
    }

    /// <summary>Unstages <paramref name="change"/> then reloads the status.</summary>
    [RelayCommand]
    public async Task UnstageAsync(GitChange change)
    {
        Report(await _write.UnstageAsync(_worktreePath, change.Path));
        await LoadAsync(_worktreePath);
    }

    /// <summary>
    /// Commits the staged files with <see cref="CommitMessage"/>. No-ops when
    /// <see cref="CanCommit"/> is false. Clears the message on success then reloads.
    /// </summary>
    [RelayCommand]
    public async Task CommitAsync()
    {
        if (!CanCommit) return;

        var r = await _write.CommitAsync(_worktreePath, CommitMessage);
        Report(r);
        if (r.Ok) CommitMessage = "";
        await LoadAsync(_worktreePath);
    }

    /// <summary>Pushes the current branch then reloads.</summary>
    [RelayCommand]
    public async Task PushAsync()
    {
        Report(await _write.PushAsync(_worktreePath));
        await LoadAsync(_worktreePath);
    }

    /// <summary>Pulls the current branch then reloads.</summary>
    [RelayCommand]
    public async Task PullAsync()
    {
        Report(await _write.PullAsync(_worktreePath));
        await LoadAsync(_worktreePath);
    }

    /// <summary>
    /// Stashes the current working-tree changes, using <see cref="CommitMessage"/> as the optional
    /// stash label when non-empty, then reloads.
    /// </summary>
    [RelayCommand]
    public async Task StashAsync()
    {
        var label = string.IsNullOrWhiteSpace(CommitMessage) ? null : CommitMessage;
        Report(await _stash.StashAsync(_worktreePath, label));
        await LoadAsync(_worktreePath);
    }

    /// <summary>Pops the most recent stash entry then reloads.</summary>
    [RelayCommand]
    public async Task StashPopAsync()
    {
        Report(await _stash.StashPopAsync(_worktreePath));
        await LoadAsync(_worktreePath);
    }
}
