namespace Styloagent.App.Services;

/// <summary>The outcome of the "open repo / second instance" gesture.</summary>
public enum OpenRepoInstanceStatus
{
    /// <summary>A repo was opened as a federated instance.</summary>
    Opened,
    /// <summary>The operator dismissed the folder picker.</summary>
    Cancelled,
    /// <summary>The picked folder is not inside a git repository.</summary>
    NotARepo,
    /// <summary>The repo has no <c>.styloagent/</c> — it isn't a Styloagent instance to federate.</summary>
    NotStyloagent,
    /// <summary>That repo instance is already open in this cockpit.</summary>
    AlreadyOpen,
    /// <summary>The federation opener threw (e.g. seam not yet wired); the operator can retry.</summary>
    Failed,
}

/// <summary>The result of <see cref="RepoInstanceCoordinator.OpenAsync"/>: a status + the resolved root.</summary>
public sealed record OpenRepoInstanceResult(OpenRepoInstanceStatus Status, string? RepoRoot = null, string? Message = null);

/// <summary>
/// Opens a repo — already resolved to its canonical git root — as an independent <b>federated instance</b>:
/// its own <c>.styloagent/</c> channel, fleet and router, watched by this cockpit (the <c>(repo, prefix)</c>
/// model in <c>PROTOCOL.md</c>). cockpit owns the UI + pane/bus federation; the per-repo instance MODEL
/// (repo → channelRoot resolver, per-repo delivery coordinator, repo-qualified routing) is <c>bus-</c>'s
/// seam. Ships today against <see cref="StubRepoInstanceOpener"/> and swaps 1:1 when that lands.
/// </summary>
public interface IRepoInstanceOpener
{
    Task OpenAsync(string repoRoot, CancellationToken ct = default);
}

/// <summary>
/// Stub opener used until <c>bus-</c>'s per-repo instance seam lands. Records the request (so the shell can
/// log/notify "opening &lt;repo&gt; — federation pending") but performs no real federation. Swap for the
/// Core-backed opener when the seam is ready; nothing above <see cref="IRepoInstanceOpener"/> changes.
/// </summary>
public sealed class StubRepoInstanceOpener : IRepoInstanceOpener
{
    private readonly Action<string>? _onOpen;
    public StubRepoInstanceOpener(Action<string>? onOpen = null) => _onOpen = onOpen;

    public Task OpenAsync(string repoRoot, CancellationToken ct = default)
    {
        _onOpen?.Invoke(repoRoot);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Orchestrates the live open-repo gesture: pick a folder → resolve its canonical git root
/// (<c>repo-</c>'s <c>ResolveRepoRootAsync</c>, null on non-repo) → confirm it has its own
/// <c>.styloagent/</c> → hand the root to an <see cref="IRepoInstanceOpener"/>, de-duping repos already
/// open. Pure orchestration over injected ports, so it is fully unit-testable without a real dialog,
/// git process, or federation.
/// </summary>
public sealed class RepoInstanceCoordinator
{
    private readonly IFolderPicker _picker;
    private readonly Func<string, CancellationToken, Task<string?>> _resolveRepoRoot;
    private readonly IRepoInstanceOpener _opener;
    private readonly Func<string, bool> _isStyloagentRepo;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    public RepoInstanceCoordinator(
        IFolderPicker picker,
        Func<string, CancellationToken, Task<string?>> resolveRepoRoot,
        IRepoInstanceOpener opener,
        Func<string, bool>? isStyloagentRepo = null)
    {
        _picker = picker;
        _resolveRepoRoot = resolveRepoRoot;
        _opener = opener;
        _isStyloagentRepo = isStyloagentRepo
            ?? (root => Directory.Exists(Path.Combine(root, ".styloagent")));
    }

    /// <summary>The repo roots currently open as federated instances (canonical roots, case-insensitive).</summary>
    public IReadOnlyCollection<string> OpenRepoRoots => _open;

    /// <summary>Run the gesture end to end; returns what happened so the shell can notify the operator.</summary>
    public async Task<OpenRepoInstanceResult> OpenAsync(CancellationToken ct = default)
    {
        var picked = await _picker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(picked))
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.Cancelled);

        var root = await _resolveRepoRoot(picked, ct);
        if (string.IsNullOrWhiteSpace(root))
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.NotARepo,
                Message: $"'{picked}' is not inside a git repository.");

        if (!_isStyloagentRepo(root))
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.NotStyloagent, root,
                $"'{root}' has no .styloagent/ — not a Styloagent instance.");

        if (!_open.Add(root))
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.AlreadyOpen, root);

        try
        {
            await _opener.OpenAsync(root, ct);
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.Opened, root);
        }
        catch (Exception ex)
        {
            _open.Remove(root);   // not actually open — let the operator retry once the cause is fixed
            return new OpenRepoInstanceResult(OpenRepoInstanceStatus.Failed, root, ex.Message);
        }
    }
}
