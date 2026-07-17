using Styloagent.Core.Seeding;
using Styloagent.Core.Workspace;

namespace Styloagent.Core.Channel;

/// <summary>
/// One repo's federated bus identity: its canonical <see cref="RepoRoot"/> (the routing/dedupe KEY), its
/// display <see cref="Name"/>, its own <see cref="ChannelRoot"/> (<c>&lt;repoRoot&gt;/.styloagent/channel</c>),
/// and the agent <see cref="Prefixes"/> live in that channel. Produced by <see cref="RepoChannelResolver"/>
/// from a repoRoot already canonicalized by repo-'s <c>ResolveRepoRootAsync</c>, so two panes opened from
/// different subfolders of one repo collapse to a single identity. cockpit keys federation on RepoRoot;
/// routing WITHIN the channel stays prefix-only, so <c>(RepoRoot, prefix)</c> = <c>(which-channel, prefix)</c>.
/// </summary>
public sealed record RepoChannel(
    string RepoRoot, string Name, string ChannelRoot, IReadOnlyList<string> Prefixes);

/// <summary>
/// Resolves a canonical repoRoot to its <see cref="RepoChannel"/>. The channelRoot is the SAME projection as
/// <see cref="WorkspaceConfig.SingleRepo"/> — one definition of where a repo's channel lives, so cockpit's
/// federation key can never drift from bus routing; the prefixes are read from the channel's own manifest
/// (<see cref="ChannelManifestSeeder"/>, the same source the primary instance's bus feed uses).
/// </summary>
public sealed class RepoChannelResolver
{
    private static readonly IReadOnlyDictionary<string, string> NoWorktrees = new Dictionary<string, string>();

    private readonly ChannelManifestSeeder _seeder;

    public RepoChannelResolver(ChannelManifestSeeder? seeder = null) => _seeder = seeder ?? new ChannelManifestSeeder();

    /// <summary>
    /// The canonical channel root for a repo — <c>&lt;repoRoot&gt;/.styloagent/channel</c>. Delegates to
    /// <see cref="WorkspaceConfig.SingleRepo"/> so there is exactly one definition of where a repo's channel lives.
    /// </summary>
    public static string ChannelRootFor(string repoRoot) => WorkspaceConfig.SingleRepo(repoRoot).ChannelRoot;

    /// <summary>Resolve <paramref name="repoRoot"/> to its channel + the agent prefixes live in it.</summary>
    public async Task<RepoChannel> ResolveAsync(string repoRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var single = WorkspaceConfig.SingleRepo(repoRoot);
        var entries = await _seeder.SeedAsync(single.ChannelRoot, NoWorktrees).ConfigureAwait(false);
        var prefixes = entries.Select(e => e.Prefix).ToList();
        return new RepoChannel(repoRoot, single.Name, single.ChannelRoot, prefixes);
    }
}
