using System.Text;

namespace Styloagent.Core.Hooks;

/// <summary>
/// Builds the re-hydration instruction an agent is re-fed when Claude Code compacts or resumes its
/// session (the <c>SessionStart</c> hook with <c>source=compact|resume</c>). Compaction is the moment
/// an agent can "compact away the reason for its existence" — its identity, scope and current work
/// state. Re-injecting this text points it straight back at its saved-context doc (per PROTOCOL.md,
/// that one file cold-starts it fully) and, in the same breath, holds it to its scope: hand off or
/// dehydrate rather than absorb out-of-scope work and dilute its context. Pure; no I/O.
/// </summary>
public static class HydrationText
{
    /// <summary>
    /// The re-hydration <c>additionalContext</c> for one agent. <paramref name="savedContextPath"/>,
    /// <paramref name="protocolPath"/> and <paramref name="channelRoot"/> are woven in when present so
    /// the agent re-reads exactly its own doc, the protocol, and its inbox before resuming.
    /// </summary>
    public static string For(string prefix, string? savedContextPath, string? protocolPath, string? channelRoot)
    {
        var sb = new StringBuilder();
        sb.Append("[styloagent re-hydration] Your context was just compacted or resumed — re-anchor BEFORE you continue, ");
        sb.Append($"or you will drift. You are the `{prefix}` agent; hold that identity and scope. ");

        if (!string.IsNullOrWhiteSpace(savedContextPath))
            sb.Append($"RE-READ your speciality context doc at `{savedContextPath}` now — per the protocol that one file cold-starts you fully (identity, scope, hard rules, current work state). ");

        if (!string.IsNullOrWhiteSpace(protocolPath))
            sb.Append($"Re-read the channel protocol at `{protocolPath}`. ");

        if (!string.IsNullOrWhiteSpace(channelRoot))
            sb.Append($"Check `{channelRoot}/inbox/{prefix}*.md` and `{channelRoot}/inbox/all-*.md`, then resume from your \"Current work state\". ");

        sb.Append("Stay strictly in scope: if work falls outside it, spin up a specialist with spawn_agent or hand it to the owning agent — absorbing out-of-scope work dilutes your context and is how agents lose themselves. ");
        sb.Append("If your responsibility is widening or your context is getting large, refresh your context doc and dehydrate rather than carrying it all.");
        return sb.ToString();
    }
}
