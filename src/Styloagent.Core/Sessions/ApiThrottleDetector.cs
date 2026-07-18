namespace Styloagent.Core.Sessions;

/// <summary>A throttle-state transition raised by <see cref="ApiThrottleDetector"/> (agent, throttled?, signature, when).</summary>
public readonly record struct ThrottleEvent(string AgentId, bool IsThrottled, string? Signature, DateTimeOffset Timestamp);

/// <summary>
/// Watches one agent's PTY output for API-error / rate-limit episodes. Claude Code does NOT fire a hook when
/// it's rate-limited — the agent looks alive but is stalled — so the only signal is the error text in the
/// output stream. Matches an EXTENSIBLE, case-insensitive signature set, debounces to ONE signal per episode
/// (a redrawn banner doesn't re-fire), and clears when the agent makes forward progress again — either an
/// explicit resume (<see cref="NoteResumed"/>, e.g. its hook state changed) or fresh non-error output after a
/// quiet gap since the last error banner. Pure logic: the caller feeds output + the current time.
/// </summary>
public sealed class ApiThrottleDetector
{
    /// <summary>Default API-error / rate-limit signatures (case-insensitive substring match). Extensible via the ctor.</summary>
    public static readonly IReadOnlyList<string> DefaultSignatures = new[]
    {
        "API Error",
        "Rate limited",
        "temporarily limiting requests",
        "Server is temporarily limiting requests",
        "overloaded",
        "overloaded_error",
        "429",
        "quota",
    };

    private readonly string _agentId;
    private readonly IReadOnlyList<string> _signatures;
    private bool _throttled;
    private string? _lastSignature;
    private DateTimeOffset _lastErrorAt;

    public ApiThrottleDetector(string agentId, IReadOnlyList<string>? signatures = null)
        => (_agentId, _signatures) = (agentId, signatures ?? DefaultSignatures);

    /// <summary>True while the agent is in a detected throttle episode.</summary>
    public bool IsThrottled => _throttled;

    /// <summary>The signature that opened the current (or most recent) episode.</summary>
    public string? LastSignature => _lastSignature;

    /// <summary>How long after the last error banner fresh non-error output counts as "resumed" (guards spinner ticks).</summary>
    public TimeSpan QuietGap { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised on every throttle-state TRANSITION (into throttled, and back out).</summary>
    public event Action<ThrottleEvent>? Changed;

    /// <summary>Feeds a chunk of the agent's PTY output at <paramref name="now"/>.</summary>
    public void Feed(string chunk, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        var sig = MatchSignature(chunk);
        if (sig is not null)
        {
            _lastErrorAt = now;                 // reset the gap on every (re)draw of the error banner
            if (!_throttled)
            {
                _throttled = true;
                _lastSignature = sig;
                Changed?.Invoke(new ThrottleEvent(_agentId, true, sig, now));
            }
            return;
        }

        // Non-error output. Real forward progress clears the throttle — but only after a quiet gap since the
        // last error banner, so a spinner tick or blank redraw between banners doesn't false-clear.
        if (_throttled && now - _lastErrorAt >= QuietGap && HasVisibleText(chunk))
            SetResumed(now);
    }

    /// <summary>Explicitly marks the agent resumed (e.g. its Claude Code hook state changed to Working).</summary>
    public void NoteResumed(DateTimeOffset now)
    {
        if (_throttled) SetResumed(now);
    }

    private void SetResumed(DateTimeOffset now)
    {
        _throttled = false;
        Changed?.Invoke(new ThrottleEvent(_agentId, false, _lastSignature, now));
    }

    private string? MatchSignature(string chunk)
    {
        foreach (var s in _signatures)
            if (chunk.Contains(s, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // Forward progress must be actual content, not cursor-movement / whitespace noise: require at least one
    // letter or digit. Keeps a blank redraw from being mistaken for the agent resuming.
    private static bool HasVisibleText(string chunk)
    {
        foreach (var c in chunk)
            if (char.IsLetterOrDigit(c)) return true;
        return false;
    }
}
