namespace Styloagent.Core.Hooks;

/// <summary>
/// Watches the shared hooks drop-directory (§4.4) and turns each <c>&lt;agentId&gt;__&lt;uuid&gt;.json</c>
/// file a spawned <c>claude</c> writes into a routed <see cref="HookEvent"/>.
///
/// Uses polling rather than <see cref="FileSystemWatcher"/> on purpose: for a status badge a
/// ~150 ms latency is irrelevant, and polling is deterministic and immune to macOS FSEvents
/// latency / dropped-event quirks. Each drop file is complete when the writing <c>cat</c> exits,
/// so a scan never sees a partial event; a consumed (or unparseable) file is deleted.
/// </summary>
public sealed class HookChannel : IAsyncDisposable
{
    private readonly string _hooksDir;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised (on a background thread) for each parsed hook event.</summary>
    public event Action<HookEvent>? EventReceived;

    /// <summary>The directory hook drop-files are written to and watched in.</summary>
    public string HooksDirectory => _hooksDir;

    public HookChannel(string hooksDir, TimeSpan? pollInterval = null)
    {
        _hooksDir = hooksDir ?? throw new ArgumentNullException(nameof(hooksDir));
        _interval = pollInterval ?? TimeSpan.FromMilliseconds(150);
        Directory.CreateDirectory(_hooksDir);
    }

    /// <summary>
    /// The <c>--settings &lt;json&gt;</c> args to append to this agent's <c>claude</c> launch. When
    /// <paramref name="hydrationFile"/> is supplied, the SessionStart hook re-injects that file's
    /// hydration text on compact/resume (the compaction guard).
    /// </summary>
    public IReadOnlyList<string> SettingsArgsFor(string agentId, string? hydrationFile = null,
        FleetPermissionMode permissionMode = FleetPermissionMode.Prompt,
        string? gateInvocation = null, string? repoRoot = null, string? caller = null)
        => HookSettings.BuildSettingsArgs(agentId, _hooksDir, hydrationFile, permissionMode, gateInvocation, repoRoot, caller);

    /// <summary>
    /// Writes <paramref name="hydrationText"/> as a JSON string to a stable per-agent file under the
    /// hooks dir and returns its path — the SessionStart compact/resume hook reads it back verbatim as
    /// <c>additionalContext</c>. Best-effort: returns null if the write fails (agent still launches).
    /// </summary>
    public string? WriteHydrationFile(string agentId, string hydrationText)
    {
        try
        {
            var path = Path.Combine(_hooksDir, $"{HookSettings.SanitizeAgentId(agentId)}.hydrate.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(hydrationText));
            return path;
        }
        catch { return null; }
    }

    /// <summary>Begins the background polling loop. Idempotent.</summary>
    public void Start() => _loop ??= Task.Run(PollLoopAsync);

    /// <summary>
    /// Scans the directory once, routing and consuming any drop files. Public so tests can drive
    /// processing deterministically without waiting on the poll interval.
    /// </summary>
    public void ScanOnce()
    {
        string[] files;
        try { files = Directory.GetFiles(_hooksDir, "*.json"); }
        catch (DirectoryNotFoundException) { return; }

        // Oldest first so events are routed in roughly the order they occurred.
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string path in files)
            TryProcess(path);
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { ScanOnce(); }
            catch { /* a bad scan must never kill the loop */ }

            try { await Task.Delay(_interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void TryProcess(string path)
    {
        string? json = TryReadAllText(path);
        if (string.IsNullOrEmpty(json))
            return; // not fully written yet — pick it up next scan

        string? agentId = HookSettings.AgentIdFromFileName(Path.GetFileName(path));
        if (agentId is not null
            && HookEventParser.TryParse(json, agentId, out HookEvent? evt)
            && evt is not null)
        {
            EventReceived?.Invoke(evt);
        }

        TryDelete(path); // consumed, or unparseable junk — remove either way
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }            // locked / mid-write / deleted since enumerate
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
