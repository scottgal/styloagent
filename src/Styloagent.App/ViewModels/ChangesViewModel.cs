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
    private readonly IGitTag? _tags;
    private string _worktreePath = string.Empty;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _loadCancellationGate = new();
    private CancellationTokenSource? _loadCancellation;

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

    [ObservableProperty] private string _suggestedTag = "";
    [ObservableProperty] private string _tagMessage = "Release";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWriteError))] private string? _writeStatus;

    public bool HasWriteError => !string.IsNullOrEmpty(WriteError);
    public bool HasTagging => _tags is not null;
    public string UnmergedBranchSummary => Branches.Count(b => !b.IsCurrent && !b.IsMerged) is var count && count > 0
        ? $"{count} unmerged" : "all merged";
    public string ExistingTagsText => Tags.Count == 0 ? "No tags" : string.Join(", ", Tags.TakeLast(4).Select(t => t.Name));

    public ObservableCollection<GitChange> Files        { get; } = new();
    public ObservableCollection<GitChange> StagedFiles  { get; } = new();
    public ObservableCollection<GitChange> UnstagedFiles { get; } = new();
    public ObservableCollection<GitBranch> Branches     { get; } = new();
    public ObservableCollection<GitTag> Tags             { get; } = new();
    public ObservableCollection<string>    Stashes      { get; } = new();

    public DiffViewModel Diff { get; } = new();

    /// <summary>True when there is at least one staged file and a non-empty commit message.</summary>
    public bool CanCommit => StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    public ChangesViewModel(IGitService git, IGitDiff diff, IGitWrite write, IGitBranch branch, IGitStash stash, IGitTag? tags = null)
    {
        _git    = git;
        _diff   = diff;
        _write  = write;
        _branch = branch;
        _stash  = stash;
        _tags = tags;
    }

    private void Report(GitResult r)
    {
        WriteError = r.Ok ? null : r.Error;
        WriteStatus = r.Ok ? "Done" : null;
    }

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
            Tags.Clear();
        }
        finally { _loadingBranches = false; }
        OnPropertyChanged(nameof(UnmergedBranchSummary));
        OnPropertyChanged(nameof(ExistingTagsText));
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
        CancellationTokenSource cancellation;
        lock (_loadCancellationGate)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = cancellation = new CancellationTokenSource();
        }

        var entered = false;
        try
        {
            await _loadGate.WaitAsync(cancellation.Token);
            entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            await LoadCoreAsync(worktreePath, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer refresh superseded this one. In particular, repeated .git watcher events must not
            // queue an ever-growing procession of status/branch/stash/tag subprocesses behind the UI.
        }
        finally
        {
            if (entered) _loadGate.Release();
            lock (_loadCancellationGate)
            {
                if (ReferenceEquals(_loadCancellation, cancellation))
                    _loadCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task LoadCoreAsync(string worktreePath, CancellationToken ct)
    {
        _worktreePath = worktreePath;

        var result = await _git.GetStatusAsync(worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.Ok || result.Value is null)
        {
            OnPropertyChanged(nameof(CanCommit));
            await LoadBranchesAsync(ct);
            await LoadStashesAsync(ct);
            await LoadTagsAsync(ct);
            return;
        }

        // Do not blank the panel at the start of every watcher refresh. Apply one completed snapshot on
        // the captured UI context; cancelled/stale loads never partially clear or repopulate collections.
        Files.Clear();
        StagedFiles.Clear();
        UnstagedFiles.Clear();
        foreach (var change in result.Value.Changes)
        {
            Files.Add(change);
            if (change.Staged)   StagedFiles.Add(change);
            if (change.Unstaged) UnstagedFiles.Add(change);
        }

        OnPropertyChanged(nameof(CanCommit));
        await LoadBranchesAsync(ct);
        await LoadStashesAsync(ct);
        await LoadTagsAsync(ct);
    }

    private async Task LoadTagsAsync(CancellationToken ct = default)
    {
        if (_tags is null || string.IsNullOrEmpty(_worktreePath)) return;
        var result = await _tags.ListTagsAsync(_worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.Ok || result.Value is null) return;
        Tags.Clear();
        foreach (var tag in result.Value.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Tags.Add(tag);
        SuggestedTag = SuggestNextTag(result.Value.Select(t => t.Name));
        OnPropertyChanged(nameof(ExistingTagsText));
    }

    [RelayCommand]
    public async Task CreateTagAsync()
    {
        if (_tags is null || string.IsNullOrWhiteSpace(SuggestedTag)) return;
        var tag = SuggestedTag.Trim();
        var r = await _tags.CreateAnnotatedTagAsync(_worktreePath, tag, string.IsNullOrWhiteSpace(TagMessage) ? tag : TagMessage.Trim());
        Report(r);
        if (r.Ok) WriteStatus = $"Created tag {tag} at HEAD";
        await LoadTagsAsync();
    }

    [RelayCommand]
    public async Task PushTagsAsync()
    {
        if (_tags is null) return;
        var r = await _tags.PushTagsAsync(_worktreePath);
        Report(r);
        if (r.Ok) WriteStatus = "Pushed tags";
    }

    internal static string SuggestNextTag(IEnumerable<string> names)
    {
        var parsed = names.Select(n => (HasV: n.StartsWith('v'), Parts: n.TrimStart('v').Split('.')))
            .Where(x => x.Parts.Length == 3 && int.TryParse(x.Parts[0], out _) && int.TryParse(x.Parts[1], out _) && int.TryParse(x.Parts[2], out _))
            .Select(x => (x.HasV, Major: int.Parse(x.Parts[0]), Minor: int.Parse(x.Parts[1]), Patch: int.Parse(x.Parts[2])))
            .OrderByDescending(x => x.Major).ThenByDescending(x => x.Minor).ThenByDescending(x => x.Patch)
            .ToList();
        if (parsed.Count == 0) return "v0.1.0";
        var highest = parsed[0];
        return $"{(highest.HasV ? "v" : "")}{highest.Major}.{highest.Minor}.{highest.Patch + 1}";
    }

    /// <summary>Fetches the stash list and repopulates <see cref="Stashes"/>.</summary>
    private async Task LoadStashesAsync(CancellationToken ct = default)
    {
        var r = await _stash.ListStashesAsync(_worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Ok || r.Value is null) return;

        Stashes.Clear();
        foreach (var entry in r.Value)
            Stashes.Add(entry);
    }

    /// <summary>Fetches the branch list and updates <see cref="Branches"/>, <see cref="CurrentBranch"/>, and <see cref="SelectedBranch"/>.</summary>
    private async Task LoadBranchesAsync(CancellationToken ct = default)
    {
        var r = await _branch.ListBranchesAsync(_worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!r.Ok || r.Value is null) return;

        _loadingBranches = true;
        try
        {
            Branches.Clear();
            foreach (var b in r.Value.OrderBy(b => b.IsCurrent ? 0 : b.IsMerged ? 2 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
                Branches.Add(b);

            CurrentBranch  = Branches.FirstOrDefault(b => b.IsCurrent)?.Name;
            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent);
        }
        finally { _loadingBranches = false; }
        OnPropertyChanged(nameof(UnmergedBranchSummary));
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
