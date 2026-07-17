namespace Styloagent.Core.Sessions;

/// <summary>
/// Decides WHEN a fleet agent should be nudged to checkpoint — commit its WIP and refresh its
/// <c>saved-context/&lt;prefix&gt;-context.md</c> resume doc — BEFORE a hard compaction throws away the
/// live context. A compaction re-loads whatever the agent last saved, so if that doc is stale the agent
/// resumes stale; nudging it to self-author a fresh doc while it still HAS the context closes that loop.
///
/// It is a pure, deterministic state machine: feed it each agent's context-fill fraction (0..1, from
/// <see cref="Styloagent.Core.Transcripts.TranscriptReader"/>) as often as you like, and it fires
/// <see cref="CheckpointNeeded"/> exactly ONCE each time an agent climbs past the soft threshold. It
/// re-arms only after the fill falls back below the threshold by a hysteresis band — i.e. after a
/// compaction actually shrank the context — so a busy agent sitting above the line is nudged once per
/// fill-up, never every tick. It only DECIDES; sending the nudge is the App/bus seam's job.
/// </summary>
public sealed class ContextCheckpointMonitor
{
    private readonly double _threshold;
    private readonly double _rearmBelow;

    // Prefixes that have fired and are waiting to re-arm (their fill hasn't dropped back below _rearmBelow yet).
    private readonly HashSet<string> _fired = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="threshold"/> is the context-fill fraction (0..1) at which an agent is nudged
    /// (default 0.80 — just above the 0.75 human scope-dilution note, so it's the "checkpoint NOW" signal).
    /// <paramref name="rearmHysteresis"/> is how far below the threshold the fill must fall before the same
    /// agent can fire again (default 0.10 → re-arm below 0.70), which a real compaction clears but ordinary
    /// jitter does not.
    /// </summary>
    public ContextCheckpointMonitor(double threshold = 0.80, double rearmHysteresis = 0.10)
    {
        _threshold = threshold;
        _rearmBelow = threshold - rearmHysteresis;
    }

    /// <summary>Raised (once per fill-up) with the agent prefix that should checkpoint before compaction.</summary>
    public event Action<string>? CheckpointNeeded;

    /// <summary>
    /// Records one context-fill sample for <paramref name="prefix"/>. Returns true (and raises
    /// <see cref="CheckpointNeeded"/>) exactly when this sample is the one that crosses the soft threshold
    /// while the agent is armed. A blank prefix (no session id yet) is ignored. Safe to call every tick.
    /// </summary>
    public bool Observe(string prefix, double fraction)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;

        if (fraction >= _threshold)
        {
            if (_fired.Add(prefix))   // first crossing since the last re-arm
            {
                CheckpointNeeded?.Invoke(prefix);
                return true;
            }
            return false;             // already fired, still above the line → no re-fire
        }

        if (fraction < _rearmBelow)
            _fired.Remove(prefix);    // fell back below the band (a compaction shrank it) → re-arm

        return false;
    }

    /// <summary>Drops an agent's state (e.g. when it exits) so a re-used prefix starts armed again.</summary>
    public void Forget(string prefix) => _fired.Remove(prefix);
}
