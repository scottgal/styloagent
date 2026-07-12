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

    [Fact]
    public void CopyTo_rewrites_hardcoded_channel_paths_to_the_snapshot()
    {
        var src = Path.Combine(Path.GetTempPath(), "chsnap-rw-src-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "chsnap-rw-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "launch-prompts"));
            // A restart prompt that hardcodes the source channel path — the exact hazard the rewrite defuses
            // (a revived agent would otherwise read AND write the live original via these paths).
            File.WriteAllText(Path.Combine(src, "launch-prompts", "foss-restart.md"),
                $"Read your context at {src}/saved-context/foss-context.md and write replies to {src}/outbox/");

            ChannelSnapshot.CopyTo(src, dest);

            var copied = File.ReadAllText(Path.Combine(dest, "launch-prompts", "foss-restart.md"));
            Assert.Contains($"{dest}/saved-context/foss-context.md", copied);
            Assert.Contains($"{dest}/outbox/", copied);
            Assert.DoesNotContain(src, copied);   // no lingering pointer to the (possibly live) original
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(src, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }
}
