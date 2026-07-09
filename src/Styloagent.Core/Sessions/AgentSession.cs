using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

public sealed class AgentSession
{
    private readonly AgentManifestEntry _manifest;
    private readonly IPtyLauncher _launcher;
    private readonly IFileWatcher _watcher;
    private readonly IReadOnlyList<string> _launchArgs;
    private IPtySession? _pty;

    /// <param name="launchArgs">
    /// Extra CLI args passed to <c>claude</c> on every spawn/rehydrate — e.g. the
    /// <c>--settings</c> hooks blob (§4.4). Empty by default so the agent stays fully functional
    /// even when hook observation is not wired.
    /// </param>
    public AgentSession(
        AgentManifestEntry manifest,
        IPtyLauncher launcher,
        IFileWatcher watcher,
        IReadOnlyList<string>? launchArgs = null)
        => (_manifest, _launcher, _watcher, _launchArgs)
            = (manifest, launcher, watcher, launchArgs ?? Array.Empty<string>());

    public SessionState State { get; private set; } = SessionState.Unspawned;

    /// <summary>The active PTY session, or null when dehydrated or unspawned.</summary>
    public IPtySession? CurrentPty { get; private set; }

    public event Action<string>? Output;

    /// <summary>Raised after <see cref="CurrentPty"/> is set, passing the new session.</summary>
    public event Action<IPtySession>? PtyStarted;

    public async Task SpawnAsync(string launchPrompt, CancellationToken ct = default)
    {
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            Command: "claude", Args: _launchArgs,
            WorkingDirectory: _manifest.Worktree, Env: null, Cols: 120, Rows: 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(launchPrompt + "\n", ct);
        CurrentPty = _pty;
        State = SessionState.Live;
        PtyStarted?.Invoke(_pty);
    }

    public async Task<bool> DehydrateAsync(TimeSpan ackTimeout, CancellationToken ct = default)
    {
        if (_pty is null || State != SessionState.Live) return false;
        await _pty.WriteAsync(
            $"Please checkpoint your context to {_manifest.SavedContextPath}, then stand by.\n", ct);
        var acked = await _watcher.WaitForChangeAsync(_manifest.SavedContextPath, ackTimeout, ct);
        if (!acked) return false;                 // never lose context — keep it live
        _pty.Output -= OnOutput;
        await _pty.DisposeAsync();
        _pty = null;
        CurrentPty = null;
        State = SessionState.Dehydrated;
        return true;
    }

    public async Task RehydrateAsync(string restartPrompt, CancellationToken ct = default)
    {
        if (State != SessionState.Dehydrated) return;
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            "claude", _launchArgs, _manifest.Worktree, null, 120, 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(restartPrompt + "\n", ct);
        CurrentPty = _pty;
        State = SessionState.Live;
        PtyStarted?.Invoke(_pty);
    }

    private void OnOutput(string chunk) => Output?.Invoke(chunk);
}
