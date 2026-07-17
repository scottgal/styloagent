using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

/// <summary>
/// Bug A — Model A cross-repo send routing. A <c>repo:</c> addressing param (a repo NAME, or blank = the
/// sender's own repo) resolves against the open federated instances to the TARGET repo's channelRoot: a
/// cross-repo message is physically written into that repo's own channel, always stamped with the SENDER's
/// repo as <c>From-Repo</c> so the reply routes home.
/// </summary>
public class RepoMessageRoutingTests
{
    private static readonly IReadOnlyList<string> NoPrefixes = new List<string>();

    private static RepoChannel Repo(string name) =>
        new($"/repos/{name}", name, $"/repos/{name}/.styloagent/channel", NoPrefixes);

    private static readonly RepoChannel Sender = Repo("styloagent");
    private static readonly RepoChannel Other = Repo("styloissues");
    private static readonly RepoChannel[] Open = { Repo("styloagent"), Repo("styloissues") };

    [Fact]
    public void Blank_repo_targets_the_senders_own_channel()
    {
        var t = RepoMessageRouting.Resolve(Sender, targetRepo: null, Open);

        Assert.NotNull(t);
        Assert.Equal(Sender.ChannelRoot, t!.ChannelRoot);
        Assert.Equal("styloagent", t.FromRepo);   // reply routes home to the sender's repo
    }

    [Fact]
    public void Named_repo_targets_that_repos_channel_and_stamps_the_senders_repo()
    {
        var t = RepoMessageRouting.Resolve(Sender, targetRepo: "styloissues", Open);

        Assert.NotNull(t);
        Assert.Equal(Other.ChannelRoot, t!.ChannelRoot);   // physically lands in styloissues' channel
        Assert.Equal("styloagent", t.FromRepo);            // but From-Repo = sender, so styloissues can reply home
    }

    [Fact]
    public void Own_repo_name_is_treated_as_intra_repo()
    {
        var t = RepoMessageRouting.Resolve(Sender, targetRepo: "styloagent", Open);

        Assert.NotNull(t);
        Assert.Equal(Sender.ChannelRoot, t!.ChannelRoot);
    }

    [Fact]
    public void Unknown_repo_resolves_to_null_so_caller_can_report_it()
    {
        var t = RepoMessageRouting.Resolve(Sender, targetRepo: "not-open", Open);
        Assert.Null(t);
    }
}
