using Styloagent.Core.Channel;
using Xunit;

public class ChannelProjectionTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "bus-channel");

    private static readonly IReadOnlyCollection<string> KnownPrefixes =
        new[] { "overview-", "foss-", "all-" };

    [Fact]
    public async Task AtomQuestion_groups_into_one_thread_with_two_messages()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.FirstOrDefault(t => t.Slug == "atom-question");
        Assert.NotNull(thread);
        Assert.Equal(2, thread.Messages.Count);
    }

    [Fact]
    public async Task AtomQuestion_thread_prefixes_contains_both_foss_and_overview()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.First(t => t.Slug == "atom-question");
        Assert.Contains("foss-", thread.Prefixes);
        Assert.Contains("overview-", thread.Prefixes);
    }

    [Fact]
    public async Task AtomQuestion_inbox_message_state_is_Replied()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.First(t => t.Slug == "atom-question");
        var inboxMsg = thread.Messages.First(m => m.Kind == BusMessageKind.Inbox);
        Assert.Equal(BusMessageState.Replied, inboxMsg.State);
    }

    [Fact]
    public async Task ProtocolUpdate_parses_as_Broadcast_with_all_prefix()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.FirstOrDefault(t => t.Slug == "protocol-update");
        Assert.NotNull(thread);
        var msg = thread.Messages.Single();
        Assert.Equal(BusMessageKind.Broadcast, msg.Kind);
        Assert.Equal("all-", msg.RoutingPrefix);
    }

    [Fact]
    public async Task ArchivedMessage_state_is_Archived()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.FirstOrDefault(t => t.Slug == "old-closed");
        Assert.NotNull(thread);
        var msg = thread.Messages.Single();
        Assert.Equal(BusMessageState.Archived, msg.State);
    }

    [Fact]
    public async Task FossInboxMessage_header_parses_From_and_Timestamp()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(FixtureRoot, KnownPrefixes);

        var thread = threads.First(t => t.Slug == "atom-question");
        var inboxMsg = thread.Messages.First(m => m.Kind == BusMessageKind.Inbox);
        Assert.NotNull(inboxMsg.From);
        Assert.Contains("overview-", inboxMsg.From);
        Assert.NotNull(inboxMsg.Timestamp);
    }

    [Fact]
    public async Task MissingChannelRoot_returns_empty_list_no_throw()
    {
        var projection = new ChannelProjection();
        var threads = await projection.ReadAsync(
            "/nonexistent/channel/path/that/does/not/exist",
            KnownPrefixes);

        Assert.Empty(threads);
    }
}
