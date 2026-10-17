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
    private string _requestedPath = string.Empty;
    private bool _hasRepository;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _loadCancellationGate = new();
    private CancellationTokenSource? _loadCancellation;
    private string _lastSuggestedTag = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _isBusy;

    public bool CanWrite => !IsBusy && _hasRepository && _worktreePath == _requestedPath;
    public string RepositoryPath => _worktreePath;

    [ObservableProperty] private string _syncSummary = "";

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
    public bool CanCommit => CanWrite && StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    public ChangesViewModel(IGitService git, IGitDiff diff, IGitWrite write, IGitBranch branch, IGitStash stash, IGitTag? tags = null)
    {
        _git    = git;
        _diff   = diff;
        _write  = write;
        _branch = branch;
        _stash  = stash;
        _tags = tags;
    }

    private async Task ExecuteWriteAsync(Func<string, Task<GitResult>> operation, string success, Action? onSuccess = null)
    {
        if (!CanWrite) return;
        var path = _worktreePath;
        IsBusy = true;
        WriteError = null;
        WriteStatus = "Working…";
        try
        {
            var result = await operation(path);
            if (_requestedPath != path) return;
            WriteError = result.Ok ? null : result.Error;
            WriteStatus = result.Ok ? success : null;
            if (result.Ok) onSuccess?.Invoke();
            await LoadAsync(path);
        }
        catch (Exception ex)
        {
            if (_requestedPath == path) { WriteError = ex.Message; WriteStatus = null; }
        }
        finally { IsBusy = false; }
    }

    /// <summary>Clears all file lists, the diff, the commit message, branch state, and any write error.</summary>
    public void Clear()
    {
        _worktreePath = string.Empty;
        _requestedPath = string.Empty;
        _hasRepository = false;
        lock (_loadCancellationGate) _loadCancellation?.Cancel();
        WriteStatus = null;
        SyncSummary = "";
        SuggestedTag = _lastSuggestedTag = "";
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(RepositoryPath));
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
        if (_requestedPath != worktreePath)
        {
            _requestedPath = worktreePath;
            CommitMessage = NewBranchName = SuggestedTag = _lastSuggestedTag = "";
            WriteStatus = WriteError = null;
            Diff.File = null;
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(CanCommit));
        }
        CancellationTokenSource cancellation;
        lock (_loadCancellationGate)
        {
            _loadCancellation?.Cancel();
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
        if (_worktreePath != worktreePath) _hasRepository = false;
        _worktreePath = worktreePath;
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(RepositoryPath));

        var result = await _git.GetStatusAsync(worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.Ok || result.Value is null)
        {
            _hasRepository = false;
            Files.Clear();
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            Branches.Clear();
            Tags.Clear();
            SyncSummary = "";
            WriteError = result.Error ?? "Could not read repository status.";
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(CanCommit));
            await LoadBranchesAsync(ct);
            await LoadStashesAsync(ct);
            await LoadTagsAsync(ct);
            return;
        }

        SyncSummary = $"↑ {result.Value.Ahead} ahead · ↓ {result.Value.Behind} behind";

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
        _hasRepository = true;
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanCommit));
    }

    private async Task LoadTagsAsync(CancellationToken ct = default)
    {
        if (_tags is null || string.IsNullOrEmpty(_worktreePath)) return;
        var result = await _tags.ListTagsAsync(_worktreePath, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.Ok || result.Value is null) return;
        Tags.Clear();
        foreach (var tag in result.Value.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Tags.Add(tag);
        var suggestion = SuggestNextTag(result.Value.Select(t => t.Name));
        if (string.IsNullOrWhiteSpace(SuggestedTag) || SuggestedTag == _lastSuggestedTag)
            SuggestedTag = suggestion;
        _lastSuggestedTag = suggestion;
        OnPropertyChanged(nameof(ExistingTagsText));
    }

    [RelayCommand]
    public async Task CreateTagAsync()
    {
        if (_tags is null || string.IsNullOrWhiteSpace(SuggestedTag)) return;
        var tag = SuggestedTag.Trim();
        var message = string.IsNullOrWhiteSpace(TagMessage) ? tag : TagMessage.Trim();
        await ExecuteWriteAsync(path => _tags.CreateAnnotatedTagAsync(path, tag, message),
            $"Created tag {tag} at HEAD", () => SuggestedTag = "");
    }

    [RelayCommand]
    public async Task PushTagsAsync()
    {
        if (_tags is null) return;
        await ExecuteWriteAsync(path => _tags.PushTagsAsync(path), "Pushed tags");
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
        await ExecuteWriteAsync(path => _branch.SwitchBranchAsync(path, b.Name), $"Switched to {b.Name}");
    }

    /// <summary>Creates a new branch from <see cref="NewBranchName"/> then reloads. No-ops on whitespace.</summary>
    [RelayCommand]
    public async Task CreateBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBranchName)) return;
        var name = NewBranchName.Trim();
        await ExecuteWriteAsync(path => _branch.CreateBranchAsync(path, name), $"Created branch {name}", () => NewBranchName = "");
    }

    /// <summary>
    /// Sets <see cref="SelectedFile"/> and loads the diff for that file.
    /// </summary>
    public async Task SelectFileAsync(GitChange file, bool? staged = null)
    {
        SelectedFile = file;
        var path = _requestedPath;
        var result = await _diff.GetDiffAsync(path, file.Path, staged: staged ?? (file.Staged && !file.Unstaged));
        if (_requestedPath == path && SelectedFile == file)
            Diff.File = result.Ok ? result.Value : null;
    }

    /// <summary>Stages <paramref name="change"/> then reloads the status.</summary>
    [RelayCommand]
    public async Task StageAsync(GitChange change)
    {
        await ExecuteWriteAsync(path => _write.StageAsync(path, change.Path), $"Staged {change.Path}");
    }

    /// <summary>Unstages <paramref name="change"/> then reloads the status.</summary>
    [RelayCommand]
    public async Task UnstageAsync(GitChange change)
    {
        await ExecuteWriteAsync(path => _write.UnstageAsync(path, change.Path), $"Unstaged {change.Path}");
    }

    /// <summary>
    /// Commits the staged files with <see cref="CommitMessage"/>. No-ops when
    /// <see cref="CanCommit"/> is false. Clears the message on success then reloads.
    /// </summary>
    [RelayCommand]
    public async Task CommitAsync()
    {
        if (!CanCommit) return;

        var message = CommitMessage;
        await ExecuteWriteAsync(path => _write.CommitAsync(path, message), "Committed staged changes", () => CommitMessage = "");
    }

    /// <summary>Pushes the current branch then reloads.</summary>
    [RelayCommand]
    public async Task PushAsync()
    {
        await ExecuteWriteAsync(path => _write.PushAsync(path), "Pushed branch");
    }

    /// <summary>Pulls the current branch then reloads.</summary>
    [RelayCommand]
    public async Task PullAsync()
    {
        await ExecuteWriteAsync(path => _write.PullAsync(path), "Pulled branch");
    }

    /// <summary>
    /// Stashes the current working-tree changes, using <see cref="CommitMessage"/> as the optional
    /// stash label when non-empty, then reloads.
    /// </summary>
    [RelayCommand]
    public async Task StashAsync()
    {
        var label = string.IsNullOrWhiteSpace(CommitMessage) ? null : CommitMessage;
        await ExecuteWriteAsync(path => _stash.StashAsync(path, label), "Stashed changes");
    }

    /// <summary>Pops the most recent stash entry then reloads.</summary>
    [RelayCommand]
    public async Task StashPopAsync()
    {
        await ExecuteWriteAsync(path => _stash.StashPopAsync(path), "Applied latest stash");
    }
}
