using Styloagent.App.ViewModels;
using Styloagent.Core.Channel;

namespace Styloagent.App.Tests;

/// <summary>
/// The real (surfacing-only) opener behind IRepoInstanceOpener: resolve a repoRoot to its OWN channel
/// via bus-'s blessed RepoChannelResolver, then hand that RepoChannel to the shell to surface as a bus
/// pane. Keyed by canonical repoRoot. Driving the instance (delivery coordinator, cross-repo messaging)
/// is the next slice; this proves the surfacing resolves the correct, drift-free channel.
/// </summary>
public class CockpitRepoInstanceOpenerTests : IDisposable
{
    private readonly string _repo;

    public CockpitRepoInstanceOpenerTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "repoinst-" + Guid.NewGuid().ToString("N"), "stylobot");
        Directory.CreateDirectory(Path.Combine(_repo, ".styloagent", "channel", "inbox"));
    }

    public void Dispose()
    {
        var top = Directory.GetParent(_repo)?.FullName;
        if (top is not null && Directory.Exists(top)) Directory.Delete(top, recursive: true);
    }

    [Fact]
    public async Task OpenAsync_SurfacesTheReposOwnChannel_KeyedByRepoRoot()
    {
        RepoChannel? surfaced = null;
        var opener = new CockpitRepoInstanceOpener(new RepoChannelResolver(),
            ch => { surfaced = ch; return Task.CompletedTask; });

        await opener.OpenAsync(_repo);

        Assert.NotNull(surfaced);
        Assert.Equal(_repo, surfaced!.RepoRoot);                                  // key = canonical repoRoot
        Assert.Equal("stylobot", surfaced.Name);                                  // display name = folder name
        // channelRoot is the SAME projection bus routing uses — it can't drift from routing.
        Assert.Equal(RepoChannelResolver.ChannelRootFor(_repo), surfaced.ChannelRoot);
    }
}
