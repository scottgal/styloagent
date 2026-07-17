using Styloagent.Core.Projects;

namespace Styloagent.Core.Channel;

/// <summary>
/// The per-repo delivery stack for one federated instance: the resolved <see cref="RepoChannel"/>, its own
/// <see cref="PendingInbox"/> (delivery state under its own hooks dir), a <see cref="MessageDeliveryService"/>,
/// and the <see cref="ChannelDeliveryCoordinator"/> watching that repo's channel. All four are bound to a
/// single repo, so the instance delivers independently of every other open repo (degrade-never-destroy per
/// repo: close the workspace and each repo still coordinates on its own bus).
/// </summary>
public sealed record RepoInstanceChannel(
    RepoChannel Channel,
    PendingInbox Pending,
    MessageDeliveryService Delivery,
    ChannelDeliveryCoordinator Coordinator);

/// <summary>
/// Builds a <see cref="RepoInstanceChannel"/> for a federated repo. This is the Core seam behind cockpit's
/// <c>IRepoInstanceOpener.OpenAsync(repoRoot)</c>: resolve the repo's channel + prefixes, then bind a fresh
/// delivery coordinator over it. cockpit calls it once per opened repo (the held N-coordinator federation
/// wiring); the App supplies the two platform seams — the PTY <see cref="IMessageInjector"/> and a live-agents
/// snapshot — exactly as the primary instance's coordinator is wired.
/// </summary>
public sealed class RepoInstanceFactory
{
    private readonly RepoChannelResolver _resolver;

    public RepoInstanceFactory(RepoChannelResolver? resolver = null) => _resolver = resolver ?? new RepoChannelResolver();

    /// <summary>
    /// Build the delivery stack bound to <paramref name="repoRoot"/>'s own channel, with a
    /// <see cref="PendingInbox"/> under <paramref name="hooksDirectory"/> (this instance's own delivery state).
    /// </summary>
    /// <param name="repoRoot">Canonical repo root (from repo-'s <c>ResolveRepoRootAsync</c>) — the routing key.</param>
    /// <param name="hooksDirectory">This instance's hooks dir; its PendingInbox lives here, isolated per repo.</param>
    /// <param name="policy">Priority ladder to apply (usually the target repo's own policy).</param>
    /// <param name="injector">App/PTY seam that types a nudge into a live session.</param>
    /// <param name="liveAgents">Snapshot of the agents live in THIS repo's instance.</param>
    public async Task<RepoInstanceChannel> CreateAsync(
        string repoRoot,
        string hooksDirectory,
        PriorityPolicy policy,
        IMessageInjector injector,
        Func<IReadOnlyList<AgentPresence>> liveAgents,
        CancellationToken ct = default)
    {
        var channel = await _resolver.ResolveAsync(repoRoot, ct).ConfigureAwait(false);
        var pending = new PendingInbox(hooksDirectory);
        var delivery = new MessageDeliveryService(policy, injector, pending);
        var coordinator = new ChannelDeliveryCoordinator(channel.ChannelRoot, channel.Prefixes, delivery, liveAgents);
        return new RepoInstanceChannel(channel, pending, delivery, coordinator);
    }
}
