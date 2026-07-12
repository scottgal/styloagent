using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

public class ChannelSnapshotTests
{
    [Fact]
    public void CopyTo_duplicates_the_channel_tree_and_leaves_the_source_untouched()
    {
        var src = Path.Combine(Path.GetTempPath(), "chsnap-src-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "chsnap-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "saved-context"));
            Directory.CreateDirectory(Path.Combine(src, "inbox"));
            File.WriteAllText(Path.Combine(src, "saved-context", "foss-context.md"), "# foss");
            File.WriteAllText(Path.Combine(src, "inbox", "all-hello.md"), "hi");
            File.WriteAllText(Path.Combine(src, "PROTOCOL.md"), "protocol");

            var result = ChannelSnapshot.CopyTo(src, dest);

            Assert.Equal(dest, result);
            Assert.True(File.Exists(Path.Combine(dest, "saved-context", "foss-context.md")));
            Assert.True(File.Exists(Path.Combine(dest, "inbox", "all-hello.md")));
            Assert.True(File.Exists(Path.Combine(dest, "PROTOCOL.md")));

            // The source is only read — writing to the copy must not reflect back.
            File.WriteAllText(Path.Combine(dest, "saved-context", "foss-context.md"), "# mutated copy");
            Assert.Equal("# foss", File.ReadAllText(Path.Combine(src, "saved-context", "foss-context.md")));
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }
}
