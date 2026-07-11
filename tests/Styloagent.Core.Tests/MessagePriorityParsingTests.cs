using Styloagent.Core.Channel;
using Xunit;

public class MessagePriorityParsingTests
{
    private static readonly IReadOnlyCollection<string> Prefixes = new[] { "alpha-" };

    private static async Task<BusMessage> ReadOneAsync(string headerBlock)
    {
        var root = Path.Combine(Path.GetTempPath(), "prio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "inbox", "alpha-topic.md"),
                headerBlock + "\n\nBody.");
            var threads = await new ChannelProjection().ReadAsync(root, Prefixes);
            return threads.SelectMany(t => t.Messages).Single();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("urgent", MessagePriority.Urgent)]
    [InlineData("URGENT", MessagePriority.Urgent)]
    [InlineData("normal", MessagePriority.Normal)]
    [InlineData("low", MessagePriority.Low)]
    [InlineData("info", MessagePriority.Info)]
    [InlineData("informational", MessagePriority.Info)]
    public async Task Priority_header_parses_to_level(string headerValue, MessagePriority expected)
    {
        var msg = await ReadOneAsync($"**From:** ops\n**Priority:** {headerValue}");
        Assert.Equal(expected, msg.Priority);
    }

    [Fact]
    public async Task Missing_priority_header_defaults_to_Normal()
    {
        var msg = await ReadOneAsync("**From:** ops");
        Assert.Equal(MessagePriority.Normal, msg.Priority);
    }

    [Fact]
    public async Task Unrecognized_priority_falls_back_to_Normal()
    {
        var msg = await ReadOneAsync("**From:** ops\n**Priority:** screaming");
        Assert.Equal(MessagePriority.Normal, msg.Priority);
    }
}
