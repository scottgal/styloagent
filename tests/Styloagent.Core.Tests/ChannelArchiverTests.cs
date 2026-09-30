using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

public sealed class ChannelArchiverTests
{
    [Fact]
    public void ArchiveThreads_handles_dashed_prefixes_and_moves_only_requested_slugs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outbox);

        try
        {
            File.WriteAllText(Path.Combine(inbox, "agent-12-release-status.md"), "release");
            File.WriteAllText(Path.Combine(inbox, "agent-12-follow-up-release-status.md"), "follow-up");
            File.WriteAllText(Path.Combine(outbox, "release-status.reply.md"), "reply");
            File.WriteAllText(Path.Combine(inbox, "agent-12-unrelated.md"), "keep");

            var moved = ChannelArchiver.ArchiveThreads(root, ["release-status"]);

            Assert.Equal(3, moved);
            Assert.True(File.Exists(Path.Combine(inbox, "agent-12-unrelated.md")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "archive", "inbox"), "*.md").Length);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "archive", "outbox"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
