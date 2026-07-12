using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

public class WorktreeMapReaderTests
{
    [Fact]
    public void Reads_prefix_to_worktree_pairs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "worktrees.yaml"),
                "foss-: /repos/stylobot\ndeploy-: /repos/infra\n");

            var map = WorktreeMapReader.Read(dir);

            Assert.Equal("/repos/stylobot", map["foss-"]);
            Assert.Equal("/repos/infra", map["deploy-"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_file_yields_an_empty_map()
        => Assert.Empty(WorktreeMapReader.Read(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"))));
}
