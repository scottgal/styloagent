using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;

namespace Styloagent.Core.Sessions;

public sealed class AgentSession
{
    private readonly AgentManifestEntry _manifest;
    private readonly IPtyLauncher _launcher;
    private readonly IFileWatcher _watcher;
    private IPtySession? _pty;

    public AgentSession(AgentManifestEntry manifest, IPtyLauncher launcher, IFileWatcher watcher)
        => (_manifest, _launcher, _watcher) = (manifest, launcher, watcher);

    public SessionState State { get; private set; } = SessionState.Unspawned;
    public event Action<string>? Output;

    public async Task SpawnAsync(string launchPrompt, CancellationToken ct = default)
    {
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            Command: "claude", Args: Array.Empty<string>(),
            WorkingDirectory: _manifest.Worktree, Env: null, Cols: 120, Rows: 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(launchPrompt + "\n", ct);
        State = SessionState.Live;
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
        State = SessionState.Dehydrated;
        return true;
    }

    public async Task RehydrateAsync(string restartPrompt, CancellationToken ct = default)
    {
        if (State != SessionState.Dehydrated) return;
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            "claude", Array.Empty<string>(), _manifest.Worktree, null, 120, 30), ct);
        _pty.Output += OnOutput;
        await _pty.WriteAsync(restartPrompt + "\n", ct);
        State = SessionState.Live;
    }

    private void OnOutput(string chunk) => Output?.Invoke(chunk);
}
