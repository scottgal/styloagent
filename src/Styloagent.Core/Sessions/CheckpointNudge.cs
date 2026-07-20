namespace Styloagent.Core.Sessions;

/// <summary>
/// Builds the canonical message an agent receives when <see cref="ContextCheckpointMonitor"/> decides it is
/// filling its context window and should checkpoint BEFORE a hard compaction discards the live context.
///
/// The wording is load-bearing on two counts:
/// 1. It tells the agent to commit WIP ATOMICALLY with <c>git commit -- &lt;paths&gt;</c>. In the shared
///    working tree every worktree:false agent shares ONE index, so "git add then pause" lets another
///    agent's commit sweep up these staged files — the exact race the fleet just hit. A pathspec commit
///    captures exactly the named files regardless of the shared index, so the nudge can't cause the race
///    it's trying to get ahead of.
/// 2. It points the agent at its own <c>saved-context/&lt;prefix&gt;-context.md</c> resume doc, because the
///    post-compaction auto-reload (SessionStart source=compact) re-injects THAT doc — refresh it now and
///    the agent resumes fresh; leave it stale and it resumes stale.
/// </summary>
public static class CheckpointNudge
{
    public static string AdaptiveBudgetFor(string prefix, ContextPressure pressure, long remainingTokens)
    {
        var remaining = remainingTokens > 0 ? $" Approximately {Format(remainingTokens)} tokens remain." : "";
        return $"⚠ Adaptive context guidance for {prefix}: {ContextPressurePolicy.Guidance(pressure)}{remaining}";
    }

    private static string Format(long tokens) => tokens >= 1000 ? $"{tokens / 1000}k" : tokens.ToString();

    /// <summary>
    /// The checkpoint-now message for <paramref name="prefix"/>. When <paramref name="contextDocPath"/> is
    /// known it names the exact resume doc to refresh; otherwise it falls back to the saved-context convention.
    /// </summary>
    public static string For(string prefix, string? contextDocPath)
    {
        string doc = string.IsNullOrWhiteSpace(contextDocPath)
            ? $"your saved-context/{prefix}context.md resume doc"
            : contextDocPath;

        return
            $"⚠ Checkpoint now, {prefix} — your context window is filling and a compaction is near. " +
            "A compaction discards the live context and auto-reloads your saved resume doc, so refresh it " +
            "BEFORE that happens or you'll resume stale. Two steps:\n" +
            "1. Commit your WIP ATOMICALLY: `git commit -- <your files>` (commits exactly your paths, so a " +
            "concurrent agent's commit can't sweep them up). Never `git add` then pause, and never -A/-am.\n" +
            $"2. Refresh {doc} with a distilled snapshot of your current task, decisions, and next step, so " +
            "the post-compaction reload restores fresh state.";
    }
}
