namespace Styloagent.Core.Channel;

/// <summary>
/// The resolved destination of a (possibly cross-repo) send: which channel to write the trace into, and the
/// repo to stamp as <c>**From-Repo:**</c> so a reply routes home.
/// </summary>
public sealed record RepoSendTarget(string ChannelRoot, string FromRepo);

/// <summary>
/// Model A cross-repo send routing. A <c>repo</c> addressing param — a repo NAME, or blank for the sender's
/// own repo — resolves against the currently-open federated instances to the TARGET repo's channelRoot. A
/// cross-repo message is physically written into the target repo's own channel (Model A: the target repo is
/// implicit in the channel location); it always carries the SENDER's repo as <c>From-Repo</c> so the reply
/// routes home. Pure — cockpit injects the open <see cref="RepoChannel"/>s; no I/O — the companion to the
/// prefix-only <see cref="MessageRouting"/> that routes WITHIN a channel.
/// </summary>
public static class RepoMessageRouting
{
    /// <summary>
    /// Resolve where a send from <paramref name="sender"/> addressed to repo <paramref name="targetRepo"/>
    /// should land. <paramref name="targetRepo"/> null/blank, or naming the sender's own repo (by name or
    /// root), is intra-repo → the sender's own channel. Returns <c>null</c> when <paramref name="targetRepo"/>
    /// names no open repo, so the caller can report "unknown repo" rather than silently dropping.
    /// </summary>
    public static RepoSendTarget? Resolve(
        RepoChannel sender, string? targetRepo, IReadOnlyCollection<RepoChannel> openRepos)
    {
        var want = targetRepo?.Trim();

        if (string.IsNullOrEmpty(want) || IsSender(sender, want))
            return new RepoSendTarget(sender.ChannelRoot, sender.Name);

        var target = openRepos.FirstOrDefault(r =>
            r.Name.Equals(want, StringComparison.OrdinalIgnoreCase) ||
            r.RepoRoot.Equals(want, StringComparison.OrdinalIgnoreCase));

        return target is null ? null : new RepoSendTarget(target.ChannelRoot, sender.Name);
    }

    private static bool IsSender(RepoChannel sender, string want) =>
        sender.Name.Equals(want, StringComparison.OrdinalIgnoreCase) ||
        sender.RepoRoot.Equals(want, StringComparison.OrdinalIgnoreCase);
}
