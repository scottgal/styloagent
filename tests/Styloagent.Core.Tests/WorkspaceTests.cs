using Styloagent.Core.Workspace;

namespace Styloagent.Core.Tests;

public class WorkspaceTests
{
    private static readonly string[] ThreeRepos = { "/ws/a", "/ws/b", "/ws/c" };
    private static readonly string[] ThreeNames = { "a", "b", "c" };
    private static readonly int[] ThreeIndexes = { 0, 1, 2 };
    private static readonly string[] TwoRepos = { "repoA", "repoB" };

    [Fact]
    public void SingleRepo_is_a_workspace_of_one_with_the_repos_own_channel()
    {
        var ws = WorkspaceConfig.SingleRepo("/Users/x/proj");
        Assert.True(ws.IsSingleRepo);
        Assert.Single(ws.Repos);
        Assert.Equal("proj", ws.Repos[0].Name);
        Assert.Equal(0, ws.Repos[0].Index);
        Assert.EndsWith(Path.Combine(".styloagent", "channel"), ws.ChannelRoot);
    }

    [Fact]
    public void For_builds_a_shared_channel_and_indexed_repos()
    {
        var ws = WorkspaceConfig.For("/ws", "mono", ThreeRepos);
        Assert.False(ws.IsSingleRepo);
        Assert.Equal("mono", ws.Name);
        Assert.Equal(3, ws.Repos.Count);
        Assert.Equal(ThreeNames, ws.Repos.Select(r => r.Name));
        Assert.Equal(ThreeIndexes, ws.Repos.Select(r => r.Index));
        Assert.EndsWith(Path.Combine(".styloagent-workspace", "channel"), ws.ChannelRoot);
    }

    [Fact]
    public void Store_round_trips_and_resolves_relative_repo_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore();
            store.SaveAsync(root, "demo", TwoRepos).GetAwaiter().GetResult();

            var ws = store.Load(root);

            Assert.NotNull(ws);
            Assert.Equal("demo", ws!.Name);
            Assert.Equal(2, ws.Repos.Count);
            // Relative repo paths resolve against the workspace root.
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "repoA")), ws.Repos[0].Path);
            Assert.Equal("repoB", ws.Repos[1].Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Load_returns_null_when_there_is_no_workspace_file()
        => Assert.Null(new WorkspaceStore().Load("/no/such/workspace"));
}
