using Styloagent.App.ViewModels;
using Styloagent.Core.Docs;

namespace Styloagent.App.Tests;

public class DocLibraryViewModelTests : IDisposable
{
    private const string RepoMd1Content = "# Repo Doc\n\nHello from repo.";
    private const string ChannelMd1Content = "# Channel Doc\n\nHello from channel.";

    private readonly string _repoRoot;
    private readonly string _channelRoot;

    public DocLibraryViewModelTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "doclib-repo-" + Guid.NewGuid().ToString("N"));
        _channelRoot = Path.Combine(Path.GetTempPath(), "doclib-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_channelRoot);
        File.WriteAllText(Path.Combine(_repoRoot, "readme.md"), RepoMd1Content);
        File.WriteAllText(Path.Combine(_channelRoot, "notes.md"), ChannelMd1Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_channelRoot)) Directory.Delete(_channelRoot, recursive: true);
    }

    // ── A fully in-memory per-directory lister so the lazy tree can be driven without a real filesystem. ──
    private sealed class FakeLister : IDocDirLister
    {
        public Dictionary<string, List<DocDirItem>> Dirs { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<DocDirItem> List(string dir)
            => Dirs.TryGetValue(dir, out var v) ? v : new List<DocDirItem>();
    }

    private static DateTimeOffset T(int day) => new(2024, 1, day, 0, 0, 0, TimeSpan.Zero);
    private static DocDirItem Folder(string root, string name, int day)
        => new(name, Path.Combine(root, name), IsFolder: true, T(day));
    private static DocDirItem Md(string root, string name, int day)
        => new(name, Path.Combine(root, name), IsFolder: false, T(day));

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        for (int w = 0; w < timeoutMs && !cond(); w += 10) await Task.Delay(10);
    }

    // ── Lazy tree (the "show everything is slow" fix): collapsed sections; children load on first expand. ──

    [Fact]
    public async Task Sections_start_collapsed_with_a_placeholder_and_load_children_on_first_expand()
    {
        const string repo = "/fake/repo";
        var lister = new FakeLister();
        lister.Dirs[repo] = new()
        {
            Folder(repo, "zsub", 3),
            Md(repo, "beta.md", 2),
            Md(repo, "alpha.md", 1),
        };

        var vm = new DocLibraryViewModel(repo, null, _ => { }, lister: lister);

        var repoSection = vm.Roots.Single(r => r.Name == "repo");
        Assert.False(repoSection.IsExpanded);                          // collapsed by default
        Assert.Single(repoSection.Children);
        Assert.True(repoSection.Children[0].IsPlaceholder);            // lazy: no real children yet

        repoSection.IsExpanded = true;                                 // first expand → load
        await WaitUntil(() => repoSection.Children.Count > 1 || (repoSection.Children.Count == 1 && !repoSection.Children[0].IsPlaceholder));

        // Real children materialized, sorted Name A–Z with folders first.
        Assert.Equal("zsub,alpha.md,beta.md", string.Join(",", repoSection.Children.Select(c => c.Name)));
        Assert.True(repoSection.Children.Single(c => c.Name == "zsub").IsFolder);
        Assert.True(repoSection.Children.Single(c => c.Name == "alpha.md").IsFile);
    }

    [Fact]
    public async Task Changing_sort_reorders_loaded_children()
    {
        const string repo = "/fake/repo";
        var lister = new FakeLister();
        lister.Dirs[repo] = new()
        {
            Md(repo, "alpha.md", 1),   // oldest
            Md(repo, "beta.md", 3),    // newest
            Md(repo, "gamma.md", 2),
        };

        var vm = new DocLibraryViewModel(repo, null, _ => { }, lister: lister);
        var repoSection = vm.Roots.Single(r => r.Name == "repo");
        repoSection.IsExpanded = true;
        await WaitUntil(() => repoSection.Children.Count == 3);

        Assert.Equal("alpha.md,beta.md,gamma.md", string.Join(",", repoSection.Children.Select(c => c.Name)));

        vm.SelectedSort = vm.SortModes.Single(s => s.Mode == DocSortMode.DateNewest);
        Assert.Equal("beta.md,gamma.md,alpha.md", string.Join(",", repoSection.Children.Select(c => c.Name)));

        vm.SelectedSort = vm.SortModes.Single(s => s.Mode == DocSortMode.NameDesc);
        Assert.Equal("gamma.md,beta.md,alpha.md", string.Join(",", repoSection.Children.Select(c => c.Name)));
    }

    [Fact]
    public async Task Search_finds_files_by_name_across_the_whole_library_including_unexpanded_folders()
    {
        const string repo = "/fake/repo";
        var sub = Path.Combine(repo, "sub");
        var lister = new FakeLister();
        lister.Dirs[repo] = new() { Md(repo, "readme.md", 1), Md(repo, "design.md", 2), Folder(repo, "sub", 3) };
        lister.Dirs[sub] = new() { Md(sub, "readme-sub.md", 1) };

        var vm = new DocLibraryViewModel(repo, null, _ => { }, lister: lister);

        vm.SearchText = "readme";
        Assert.True(vm.ShowSearchResults);
        await WaitUntil(() => vm.SearchResults.Count >= 2);   // background name index → results

        Assert.Contains(vm.SearchResults, e => e.Title == "readme.md");
        Assert.Contains(vm.SearchResults, e => e.Title == "readme-sub.md");   // in an unexpanded subfolder
        Assert.DoesNotContain(vm.SearchResults, e => e.Title == "design.md");

        vm.SearchText = "";
        Assert.False(vm.ShowSearchResults);
        Assert.Empty(vm.SearchResults);
    }

    [Fact]
    public async Task OpenDoc_InvokesCallback_WithTheBuiltDocument()
    {
        MarkdownDocumentViewModel? received = null;
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, docVm => received = docVm);

        var entry = new DocEntry("readme.md", Path.Combine(_repoRoot, "readme.md"), DocSource.Repo, "readme.md");
        await vm.OpenDocAsync(entry);   // reads the file OFF the UI thread, marshals the VM back to open it

        Assert.NotNull(received);
        Assert.Equal("readme.md", received!.Title);
        Assert.Equal(RepoMd1Content, received.Markdown);
    }

    // ── MarkdownDocumentViewModel (unchanged; the doc a leaf opens into) ──

    [Fact]
    public void MarkdownDocumentViewModel_LoadsFileText()
    {
        var path = Path.Combine(_repoRoot, "readme.md");
        var docVm = new MarkdownDocumentViewModel("readme.md", path);
        Assert.Equal(RepoMd1Content, docVm.Markdown);
        Assert.Equal("readme.md", docVm.Title);
        Assert.Equal(_repoRoot, docVm.SourcePath);
    }

    [Fact]
    public void MarkdownDocumentViewModel_Refresh_ReReadsFile()
    {
        var path = Path.Combine(_repoRoot, "readme.md");
        var docVm = new MarkdownDocumentViewModel("readme.md", path);
        Assert.Equal(RepoMd1Content, docVm.Markdown);

        const string updated = "# Updated\n\nNew content.";
        File.WriteAllText(path, updated);
        docVm.Refresh();
        Assert.Equal(updated, docVm.Markdown);
    }

    [Fact]
    public void MarkdownDocumentViewModel_MissingFile_YieldsEmptyString()
    {
        var docVm = new MarkdownDocumentViewModel("missing.md", "/nonexistent/path/missing.md");
        Assert.Equal(string.Empty, docVm.Markdown);
    }
}
