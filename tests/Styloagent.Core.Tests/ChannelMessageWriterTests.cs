using System.Globalization;
using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

public class ChannelMessageWriterTests
{
    private static readonly string[] Prefixes = { "foss-", "router-" };

    private static string TempChannel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "styloagent-msgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Written_message_is_read_back_by_the_projection()
    {
        var root = TempChannel();
        try
        {
            var path = ChannelMessageWriter.Write(
                root, from: "foss-", to: "router-", subject: "Need a review",
                body: "The PR is up.", priority: "urgent", timestamp: DateTimeOffset.Now);

            Assert.True(File.Exists(path));
            Assert.EndsWith("router-need-a-review.md", path);   // <to><slug>.md in inbox

            var threads = await new ChannelProjection().ReadAsync(root, Prefixes);
            var msg = threads.SelectMany(t => t.Messages).Single();

            Assert.Equal("router-", msg.RoutingPrefix);          // routes to router-
            Assert.Equal("foss-", msg.From);                     // caller is the sender
            Assert.Equal("need-a-review", msg.Slug);
            Assert.Equal(MessagePriority.Urgent, msg.Priority);
            Assert.Contains("The PR is up.", msg.Body);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Repeated_subject_does_not_overwrite_the_earlier_message()
    {
        var root = TempChannel();
        try
        {
            var now = DateTimeOffset.Now;
            var a = ChannelMessageWriter.Write(root, "foss-", "router-", "status", "one", "normal", now);
            var b = ChannelMessageWriter.Write(root, "foss-", "router-", "status", "two", "normal", now);

            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Recipient_without_trailing_dash_is_normalized()
    {
        Assert.Equal("router-", ChannelMessageWriter.NormalizeRecipient("router"));
        Assert.Equal("router-", ChannelMessageWriter.NormalizeRecipient("router-"));
        Assert.Equal("all-", ChannelMessageWriter.NormalizeRecipient(""));
        Assert.Equal("all-", ChannelMessageWriter.NormalizeRecipient("all"));
    }

    [Fact]
    public void Broadcast_message_routes_to_all()
    {
        var root = TempChannel();
        try
        {
            var path = ChannelMessageWriter.Write(
                root, "overview-", "all", "heads up", "deploying now", "info", DateTimeOffset.Now);
            Assert.Contains("all-heads-up.md", path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reply_marks_the_matching_thread_done_without_mutating_the_original_message()
    {
        var root = TempChannel();
        try
        {
            var inbound = ChannelMessageWriter.Write(root, "foss-", "router-", "Need a review", "Please review.", "normal", DateTimeOffset.Now);
            var reply = ChannelMessageWriter.Reply(root, "router-", "Need a review", "Reviewed. Next: merge.", DateTimeOffset.Now);

            Assert.True(File.Exists(inbound));
            Assert.EndsWith("need-a-review.reply.md", reply);
            var thread = (await new ChannelProjection().ReadAsync(root, Prefixes)).Single();
            Assert.Contains(thread.Messages, m => m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.Replied);
            Assert.Contains(thread.Messages, m => m.Kind == BusMessageKind.Reply);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
