using System.Globalization;

namespace Styloagent.Core.Channel;

/// <summary>What <see cref="InPlaceCheckpoint.Write"/> did.</summary>
public enum InPlaceCheckpointOutcome
{
    /// <summary>The doc was missing/blank, so a fallback anchor was written.</summary>
    Wrote,
    /// <summary>An agent-authored doc already existed and was preserved untouched.</summary>
    KeptExisting,
    /// <summary>Best-effort write couldn't proceed (no path, or an I/O error) — nothing was destroyed.</summary>
    Failed,
}

/// <summary>The outcome of a checkpoint-in-place, with the doc path and a one-line detail for the timeline.</summary>
public sealed record InPlaceCheckpointResult(InPlaceCheckpointOutcome Outcome, string? Path, string Detail);

/// <summary>
/// The PreCompact <b>fallback</b> for the compaction-resilience feature (session-'s
/// <c>ContextCheckpointMonitor</c> raises the 0.80 nudge; this catches the case where a compaction fires
/// before the agent self-authored a fresh resume doc). It snapshots a best-effort resume anchor into the
/// agent's <c>saved-context/&lt;prefix&gt;-context.md</c> so the post-compaction auto-reload isn't EMPTY.
///
/// Two invariants make it safe to call blindly from a PreCompact observer:
/// <list type="bullet">
/// <item><b>Never frees the terminal</b> — it only writes a file, unlike <c>dehydrate</c> which suspends the PTY.</item>
/// <item><b>Degrade-never-destroy</b> — it NEVER overwrites an existing agent-authored doc (even a stale one is
/// richer than a mechanical anchor); it writes only when the doc is missing or blank, and never throws.</item>
/// </list>
/// The caller supplies <paramref name="fallbackBody"/> (e.g. the agent's hydration text) so this stays
/// dependency-free.
/// </summary>
public static class InPlaceCheckpoint
{
    public static InPlaceCheckpointResult Write(
        string prefix, string? savedContextPath, string fallbackBody, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(savedContextPath))
            return new(InPlaceCheckpointOutcome.Failed, null, $"{prefix}: no saved-context path to checkpoint into");

        try
        {
            if (File.Exists(savedContextPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(savedContextPath)))
                return new(InPlaceCheckpointOutcome.KeptExisting, savedContextPath,
                    $"{prefix}: kept the existing resume doc (an agent-authored doc beats a mechanical anchor)");

            var dir = Path.GetDirectoryName(savedContextPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(savedContextPath, Anchor(prefix, fallbackBody, now));
            return new(InPlaceCheckpointOutcome.Wrote, savedContextPath, $"{prefix}: wrote a fallback checkpoint");
        }
        catch (Exception ex)
        {
            return new(InPlaceCheckpointOutcome.Failed, savedContextPath, $"{prefix}: checkpoint-in-place failed — {ex.Message}");
        }
    }

    private static string Anchor(string prefix, string fallbackBody, DateTimeOffset now) =>
        $"# {prefix} auto-checkpoint — {now.ToString("o", CultureInfo.InvariantCulture)}\n\n" +
        "⚠ Your live context was compacted before you saved a fresh resume doc (the checkpoint nudge was " +
        "missed). This is a best-effort fallback so you don't resume empty — reload your context and call " +
        "`check_inbox`, then re-establish your current task before continuing.\n" +
        (string.IsNullOrWhiteSpace(fallbackBody) ? "" : "\n" + fallbackBody.Trim() + "\n");
}
