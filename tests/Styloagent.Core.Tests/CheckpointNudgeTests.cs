using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

/// <summary>
/// The canonical message an agent is sent when <see cref="ContextCheckpointMonitor"/> fires at ~0.80 fill:
/// "checkpoint NOW before the hard compaction." It must (1) tell the agent to commit WIP ATOMICALLY with
/// <c>git commit -- &lt;paths&gt;</c> — NOT stage-and-pause, which in the shared working tree lets another
/// agent's commit sweep up its files — and (2) point it at its own resume doc so the post-compaction
/// auto-reload restores fresh state, not stale.
/// </summary>
public class CheckpointNudgeTests
{
    [Fact]
    public void Nudge_tells_the_agent_to_commit_atomically_and_refresh_its_resume_doc()
    {
        var msg = CheckpointNudge.For("session-", "/ch/saved-context/session-context.md");

        Assert.Contains("session-", msg);
        Assert.Contains("/ch/saved-context/session-context.md", msg);   // refresh YOUR doc
        // Atomic-commit instruction — the guard against the shared-tree stage-and-pause race.
        Assert.Contains("git commit --", msg);
        Assert.Contains("compact", msg, StringComparison.OrdinalIgnoreCase);   // WHY: a compaction is near
    }

    [Fact]
    public void Nudge_warns_against_stage_and_pause()
    {
        var msg = CheckpointNudge.For("bus-", "/ch/saved-context/bus-context.md");
        // Must steer away from `git add` + pause and the blanket -A/-am forms that cause the race.
        Assert.DoesNotContain("git add -A", msg);
        Assert.Contains("atomic", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nudge_degrades_without_a_doc_path()
    {
        // No saved-context path known yet — still a usable nudge naming the agent + the commit discipline.
        var msg = CheckpointNudge.For("overview-", null);
        Assert.Contains("overview-", msg);
        Assert.Contains("git commit --", msg);
        Assert.Contains("saved-context", msg);   // still points at the convention
    }
}
