using Styloagent.Core.Channel;
using Xunit;

namespace Styloagent.Core.Tests;

public class BusThreadClassifierTests
{
    private static BusMessage Msg(
        BusMessageKind kind, BusMessageState state,
        string prefix = "alpha-", string slug = "alpha-topic", DateTimeOffset? ts = null)
        => new(slug, prefix, kind, state, "/f.md", ts, "sender", "body");

    private static BusThread Thread(params BusMessage[] msgs)
        => new(msgs[0].Slug, msgs, msgs.Select(m => m.RoutingPrefix).Distinct().ToList());

    [Fact]
    public void UnrepliedInbox_IsAttention_WithFilledDot()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.Inbox, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Attention, v.Section);
        Assert.Equal("●", v.Glyph);
    }

    [Fact]
    public void RepliedInbox_IsRecent_WithReplyGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.Replied),
            Msg(BusMessageKind.Reply, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("↩", v.Glyph);
    }

    [Fact]
    public void Broadcast_IsRecent_WithBroadcastGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.Broadcast, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("◆", v.Glyph);
    }

    [Fact]
    public void AllArchived_IsArchive_WithArchiveGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.Archived),
            Msg(BusMessageKind.Reply, BusMessageState.Archived)));
        Assert.Equal(BusThreadSection.Archive, v.Section);
        Assert.Equal("▤", v.Glyph);
    }

    [Fact]
    public void PlainFollowUp_IsRecent_WithOpenDot()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.FollowUp, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("○", v.Glyph);
    }

    [Fact]
    public void Subject_PrettifiesSlug()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.New, slug: "alpha-hello-world")));
        Assert.Equal("alpha hello world", v.Subject);
    }

    [Fact]
    public void LastActivity_IsMaxTimestamp()
    {
        var t1 = DateTimeOffset.Parse("2024-01-10T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2024-01-10T11:00:00Z");
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.New, ts: t1),
            Msg(BusMessageKind.Reply, BusMessageState.New, ts: t2)));
        Assert.Equal(t2, v.LastActivity);
    }
}
