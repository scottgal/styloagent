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

    // Enter in a terminal TUI is carriage-return (0x0D), NOT line-feed (0x0A). Claude Code's input
    // box treats a bare "\n" as "insert a newline in the buffer" — so the prompt is typed but never
    // submitted, leaving stray text in the window (and blocking auto-rehydration). "\r" submits.
    private const string Submit = "\r";

    // Injecting the prompt the instant the PTY spawns types the text but the Enter is dropped — Claude's
    // TUI isn't accepting input yet, so the agent sits idle with an unsent prompt. Production waits for the
    // TUI to come up before pressing Enter (and presses once more as a safety net). Tests leave these zero
    // (the FakeLauncher never runs a real claude), so the suite stays fast. Set once by the app at startup.
    public static TimeSpan InjectSettleDelay { get; set; } = TimeSpan.Zero;
    public static TimeSpan InjectEnterRetryDelay { get; set; } = TimeSpan.Zero;

    // The PTY must spawn at ~the terminal's real grid, or claude draws its banner at one width and we
    // resize to another — reflowing the banner into wrapped garbage. We can't know the exact size before
    // the view lays out, so seed a classic 80×24 (fits the default pane without reflow) and let the terminal
    // publish its real grid here as soon as it measures, so later spawns match. Clamped to sane bounds.
    private static int _initialCols = 80;
    private static int _initialRows = 24;

    /// <summary>Publishes the terminal's real grid size so subsequent agent spawns start at the right width.</summary>
    public static void SetInitialGrid(int cols, int rows)
    {
        if (cols >= 20 && cols <= 400) _initialCols = cols;
        if (rows >= 6 && rows <= 200) _initialRows = rows;
    }

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
        // Already running — re-spawning would overwrite _pty and orphan the live process (and leak its
        // Output handler). Spawn is only valid from a non-Live state.
        if (State == SessionState.Live) return;
        _pty = await _launcher.SpawnAsync(new PtySpawnOptions(
            Command: "claude", Args: _launchArgs,
            WorkingDirectory: _manifest.Worktree, Env: null, Cols: _initialCols, Rows: _initialRows), ct);
        _pty.Output += OnOutput;
        await InjectPromptAsync(_pty, launchPrompt, ct);
        CurrentPty = _pty;
        State = SessionState.Live;
        PtyStarted?.Invoke(_pty);
    }

    /// <summary>
    /// Types a prompt into the agent's terminal and submits it: the prompt text, then Enter as a
    /// separate write. Interior newlines in a multi-line prompt stay as buffer content; only the
    /// trailing <see cref="Submit"/> (0x0D) submits — so nothing is left unsent in the input box.
    /// </summary>
    private static async Task InjectPromptAsync(IPtySession pty, string prompt, CancellationToken ct)
    {
        await pty.WriteAsync(prompt, ct);
        // Wait for the TUI to be ready before submitting, or the Enter is dropped and the prompt lingers.
        if (InjectSettleDelay > TimeSpan.Zero) await Task.Delay(InjectSettleDelay, ct);
        await pty.WriteAsync(Submit, ct);
        // Safety net: press Enter once more after the TUI has certainly settled — a no-op on an empty box.
        if (InjectEnterRetryDelay > TimeSpan.Zero)
        {
            await Task.Delay(InjectEnterRetryDelay, ct);
            await pty.WriteAsync(Submit, ct);
        }
    }

    public async Task<bool> DehydrateAsync(TimeSpan ackTimeout, CancellationToken ct = default)
    {
        if (_pty is null || State != SessionState.Live) return false;
        // No checkpoint target (e.g. the overview agent) — cannot dehydrate; keep it live rather than
        // ask it to checkpoint to nowhere and then watch an empty path.
        if (string.IsNullOrWhiteSpace(_manifest.SavedContextPath)) return false;
        await InjectPromptAsync(_pty,
            $"Please checkpoint your context to {_manifest.SavedContextPath}, then stand by.", ct);
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
            "claude", _launchArgs, _manifest.Worktree, null, _initialCols, _initialRows), ct);
        _pty.Output += OnOutput;
        await InjectPromptAsync(_pty, restartPrompt, ct);
        CurrentPty = _pty;
        State = SessionState.Live;
        PtyStarted?.Invoke(_pty);
    }

    private void OnOutput(string chunk) => Output?.Invoke(chunk);
}
