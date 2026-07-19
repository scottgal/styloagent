using Styloagent.Core.Docs;
using Styloagent.Core.Workspace;

namespace Styloagent.Core.Tests;

public class DocumentLibraryRootsTests
{
    private static readonly string[] MultiRepoPaths = ["/work/api", "/other/api", "/work/web"];
    private static readonly string[] ExpectedMultiRepoNames = ["api (1)", "api (2)", "web"];
    private static readonly int[] ExpectedMultiRepoIndexes = [0, 1, 2];

    [Fact]
    public void For_single_repo_preserves_the_repo_section_and_uses_its_own_roots()
    {
        var workspace = WorkspaceConfig.SingleRepo("/work/alpha");

        var roots = DocumentLibraryRoots.For(workspace);

        var repo = Assert.Single(roots.Repositories);
        Assert.Equal("repo", repo.DisplayName);
        Assert.Equal("/work/alpha", repo.RepoRoot);
        Assert.Equal(0, repo.RepoIndex);
        Assert.Equal(workspace.ChannelRoot, roots.ChannelRoot);
        Assert.Equal(Path.Combine("/work/alpha", ".styloagent", "logs"), roots.LogsRoot);
    }

    [Fact]
    public void For_multi_repo_keeps_each_root_separate_and_disambiguates_duplicate_names()
    {
        var workspace = WorkspaceConfig.For("/work", "suite", MultiRepoPaths);

        var roots = DocumentLibraryRoots.For(workspace);

        Assert.Equal(ExpectedMultiRepoNames, roots.Repositories.Select(r => r.DisplayName));
        Assert.Equal(MultiRepoPaths, roots.Repositories.Select(r => r.RepoRoot));
        Assert.Equal(ExpectedMultiRepoIndexes, roots.Repositories.Select(r => r.RepoIndex));
        Assert.Equal(workspace.ChannelRoot, roots.ChannelRoot);
        Assert.Equal(Path.Combine("/work", ".styloagent-workspace", "logs"), roots.LogsRoot);
    }
}
