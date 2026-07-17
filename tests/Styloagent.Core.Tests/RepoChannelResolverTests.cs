using Styloagent.Core.Channel;
using Styloagent.Core.Workspace;

namespace Styloagent.Core.Tests;

/// <summary>
/// Bug A piece 1 — the repo→channel resolver cockpit uses to key a federated instance. Given a canonical
/// repoRoot it returns that repo's channelRoot + the agent prefixes live in it; the channelRoot is the same
/// projection as <see cref="WorkspaceConfig.SingleRepo"/> so cockpit's federation key can never drift from
/// bus routing.
/// </summary>
public class RepoChannelResolverTests
{
    private static readonly string[] ExpectedPrefixes = { "bus-", "overview-" };

    private static string TempRepo(params string[] prefixes)
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-repochan-" + Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(root, ".styloagent", "channel", "saved-context");
        Directory.CreateDirectory(savedContext);
        foreach (var p in prefixes)
            File.WriteAllText(Path.Combine(savedContext, $"{p}context.md"), "ctx");
        return root;
    }

    [Fact]
    public async Task Resolves_channel_root_name_and_prefixes_from_repo_root()
    {
        var repoRoot = TempRepo("overview-", "bus-");
        try
        {
            var rc = await new RepoChannelResolver().ResolveAsync(repoRoot);

            Assert.Equal(repoRoot, rc.RepoRoot);
            Assert.Equal(Path.Combine(repoRoot, ".styloagent", "channel"), rc.ChannelRoot);
            Assert.Equal(Path.GetFileName(repoRoot), rc.Name);
            Assert.Equal(ExpectedPrefixes, rc.Prefixes.OrderBy(p => p, StringComparer.Ordinal));
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task Channel_root_matches_WorkspaceConfig_SingleRepo_so_cockpit_keying_agrees()
    {
        var repoRoot = TempRepo("overview-");
        try
        {
            var rc = await new RepoChannelResolver().ResolveAsync(repoRoot);
            Assert.Equal(WorkspaceConfig.SingleRepo(repoRoot).ChannelRoot, rc.ChannelRoot);
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }

    [Fact]
    public async Task Missing_channel_resolves_with_no_prefixes_but_still_projects_the_channel_root()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "styloagent-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        try
        {
            var rc = await new RepoChannelResolver().ResolveAsync(repoRoot);
            Assert.Empty(rc.Prefixes);
            Assert.Equal(Path.Combine(repoRoot, ".styloagent", "channel"), rc.ChannelRoot);
        }
        finally { Directory.Delete(repoRoot, recursive: true); }
    }
}
