using System.Text.Json;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Operator-side <b>view-state</b> for bus threads — has the operator <i>seen</i> a thread, and have
/// they explicitly <i>archived</i> it. This is a separate fact from a message's content-derived state
/// (<c>New</c>/<c>Replied</c>/<c>Archived</c>, owned by <c>bus-</c>'s Core classifier): "seen" is about
/// the human's eyes, not the conversation.
/// <para>
/// This is the App-side <b>port</b> the bus viewer binds to. <c>bus-</c> owns the real Core store and
/// its on-disk representation; the viewer ships today against the <see cref="InMemoryBusViewState"/>
/// fake and swaps to the Core-backed adapter 1:1 when that verb lands — nothing above this seam changes.
/// </para>
/// </summary>
public interface IBusViewState
{
    /// <summary>
    /// Has the operator viewed this thread since its most recent activity? Keyed by thread slug.
    /// <paramref name="lastActivity"/> is the thread's newest message timestamp: seeing a thread only
    /// "sticks" up to the activity present when it was viewed, so a fresh inbound un-sees it (→ WAITING).
    /// </summary>
    bool IsSeen(string threadKey, DateTimeOffset? lastActivity);

    /// <summary>Has the operator explicitly archived (dismissed) this thread? Keyed by thread slug.</summary>
    bool IsArchived(string threadKey);

    /// <summary>Record that the operator viewed a thread, as of its latest activity (a seen-watermark).</summary>
    void MarkSeen(string threadKey, DateTimeOffset? asOf);

    /// <summary>Record an explicit operator archive of a thread.</summary>
    void Archive(string threadKey);

    /// <summary>Raised when view-state changes, so the viewer can refresh live (mirrors bus-'s notify).</summary>
    event Action? Changed;
}

/// <summary>
/// In-memory <see cref="IBusViewState"/> — the FAKE the bus viewer binds to until <c>bus-</c>'s Core
/// store lands. Faithful to the contract (seen-watermark semantics, per-thread archive, change notify)
/// so it can be replaced without touching the viewer.
/// </summary>
public sealed class InMemoryBusViewState : IBusViewState
{
    // Seen-watermark per thread: the latest activity present when the operator last viewed it.
    private readonly Dictionary<string, DateTimeOffset> _seenUpTo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _archived = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event Action? Changed;

    public bool IsSeen(string threadKey, DateTimeOffset? lastActivity)
    {
        lock (_lock)
        {
            if (!_seenUpTo.TryGetValue(threadKey, out var watermark))
                return false;
            // No activity timestamp (undated thread) ⇒ any prior view counts as seen.
            return lastActivity is not { } activity || watermark >= activity;
        }
    }

    public bool IsArchived(string threadKey)
    {
        lock (_lock) return _archived.Contains(threadKey);
    }

    public void MarkSeen(string threadKey, DateTimeOffset? asOf)
    {
        // Null activity ⇒ pin the watermark at max so IsSeen holds regardless of (absent) timestamps.
        var stamp = asOf ?? DateTimeOffset.MaxValue;
        lock (_lock)
        {
            if (_seenUpTo.TryGetValue(threadKey, out var existing) && existing >= stamp)
                return;
            _seenUpTo[threadKey] = stamp;
        }
        Changed?.Invoke();
    }

    public void Archive(string threadKey)
    {
        lock (_lock)
        {
            if (!_archived.Add(threadKey))
                return;
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Durable, file-backed <see cref="IBusViewState"/> — the store the shipped viewer uses. Operator
/// seen/archived read-state is persisted to a small JSON (default <c>.styloagent/bus-view-state.json</c>)
/// so the operator's Archive (and seen-watermarks) survive a cockpit restart. It is operator-LOCAL: no
/// Core verb, no channel-file moves — <see cref="ChannelProjection"/> stays independent, this store wins
/// for the operator's view. Fail-open: any read/write I/O error degrades to empty/in-memory rather than
/// throwing, so a corrupt or unwritable file never breaks the bus viewer.
/// </summary>
public sealed class JsonBusViewState : IBusViewState
{
    private readonly string _path;
    private readonly Dictionary<string, DateTimeOffset> _seenUpTo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _archived = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public event Action? Changed;

    public JsonBusViewState(string path)
    {
        _path = path;
        Load();
    }

    public bool IsSeen(string threadKey, DateTimeOffset? lastActivity)
    {
        lock (_lock)
        {
            if (!_seenUpTo.TryGetValue(threadKey, out var watermark))
                return false;
            return lastActivity is not { } activity || watermark >= activity;
        }
    }

    public bool IsArchived(string threadKey)
    {
        lock (_lock) return _archived.Contains(threadKey);
    }

    public void MarkSeen(string threadKey, DateTimeOffset? asOf)
    {
        var stamp = asOf ?? DateTimeOffset.MaxValue;
        lock (_lock)
        {
            if (_seenUpTo.TryGetValue(threadKey, out var existing) && existing >= stamp)
                return;
            _seenUpTo[threadKey] = stamp;
            Save();
        }
        Changed?.Invoke();
    }

    public void Archive(string threadKey)
    {
        lock (_lock)
        {
            if (!_archived.Add(threadKey))
                return;
            Save();
        }
        Changed?.Invoke();
    }

    // On-disk shape: a plain DTO (case-insensitive maps are rebuilt on load, since the comparer isn't serialized).
    private sealed class Dto
    {
        public Dictionary<string, DateTimeOffset> SeenUpTo { get; set; } = new();
        public List<string> Archived { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), JsonOpts);
            if (dto is null)
                return;
            foreach (var (k, v) in dto.SeenUpTo)
                _seenUpTo[k] = v;
            foreach (var k in dto.Archived)
                _archived.Add(k);
        }
        catch
        {
            // Corrupt/unreadable file → start empty. The operator loses nothing but stale view-state.
        }
    }

    // Caller holds _lock. Writes atomically (temp + move) so a crash mid-write can't corrupt the store.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var dto = new Dto
            {
                SeenUpTo = new Dictionary<string, DateTimeOffset>(_seenUpTo),
                Archived = _archived.ToList(),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Unwritable location → keep the in-memory state; the view still works this session.
        }
    }
}
