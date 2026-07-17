using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Sessions;

/// <summary>
/// The fallback half of compaction-resilience pre-save: a raw, best-effort snapshot written right before a
/// hard compaction, for the case where the 0.80 <see cref="ContextCheckpointMonitor"/> nudge was missed and
/// the agent's <c>saved-context/&lt;prefix&gt;-context.md</c> would otherwise be empty when the
/// post-compaction auto-reload re-injects it.
///
/// At PreCompact the agent cannot take a turn (it is mid-compaction), so — unlike the nudge path, which
/// asks the agent to self-author a distilled resume doc — this reads what already exists: the agent's recent
/// <see cref="TranscriptReader"/> transcript. It writes that raw tail to the resume doc ONLY when the doc is
/// missing or empty, so it can never clobber a fresher agent-authored doc (degrade-never-destroy). Any
/// failure is swallowed — a bad snapshot must never crash the compacting agent.
/// </summary>
public static class PreCompactSnapshot
{
    /// <summary>
    /// Writes a raw fallback snapshot for <paramref name="prefix"/> from <paramref name="transcriptPath"/>
    /// into <paramref name="savedContextPath"/>, but only if that doc is missing/empty. Returns true iff it
    /// actually wrote. Best-effort: never throws.
    /// </summary>
    public static bool Capture(string prefix, string? transcriptPath, string? savedContextPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(savedContextPath)) return false;

            // Degrade-never-destroy: a doc that already has real content is the agent's own checkpoint —
            // never overwrite it with a rawer snapshot. Only fill a missing/empty one so reload isn't empty.
            if (File.Exists(savedContextPath))
            {
                var existing = File.ReadAllText(savedContextPath);
                if (!string.IsNullOrWhiteSpace(existing)) return false;
            }

            var text = TranscriptReader.ReadLastAssistantText(transcriptPath);
            if (string.IsNullOrWhiteSpace(text)) return false;   // nothing to snapshot — leave the doc untouched

            var body =
                $"<!-- raw pre-compaction fallback snapshot for {prefix}: the checkpoint nudge was missed, so " +
                "this is the agent's last transcript turn, NOT a distilled resume doc. Treat as a hint. -->\n\n" +
                $"# {prefix} — pre-compaction fallback snapshot\n\n" +
                text + "\n";

            var dir = Path.GetDirectoryName(savedContextPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(savedContextPath, body);
            return true;
        }
        catch
        {
            return false;   // best-effort — never let a snapshot failure take down the compacting agent
        }
    }
}
