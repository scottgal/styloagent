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
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    // The library now enumerates OFF the UI thread (BUG 2 fix), so population is async — await RefreshAsync
    // to settle it deterministically before asserting (the enumeration no longer completes in the ctor).
    [Fact]
    public async Task Groups_ArePopulated_BySource()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Groups.Count);
        Assert.Contains(vm.Groups, g => g.Header == "repo");
        Assert.Contains(vm.Groups, g => g.Header == "channel");
    }

    [Fact]
    public async Task Groups_RepoIsFirst()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
        await vm.RefreshAsync();

        Assert.Equal("repo", vm.Groups[0].Header);
        Assert.Equal("channel", vm.Groups[1].Header);
    }

    [Fact]
    public async Task OpenDoc_InvokesCallback_WithCorrectViewModel()
    {
        MarkdownDocumentViewModel? received = null;
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, docVm => received = docVm);
        await vm.RefreshAsync();

        var repoGroup = vm.Groups[0];
        Assert.True(repoGroup.Entries.Count > 0);

        var entry = repoGroup.Entries[0];
        await vm.OpenDocAsync(entry);   // open reads the file OFF the UI thread, then marshals back

        Assert.NotNull(received);
        Assert.Equal(entry.Title, received.Title);
        Assert.Equal(RepoMd1Content, received.Markdown);
    }

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

    [Fact]
    public async Task Refresh_RebuildsGroups()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
        await vm.RefreshAsync();
        var initialCount = vm.Groups[0].Entries.Count;

        File.WriteAllText(Path.Combine(_repoRoot, "extra.md"), "# Extra");

        await vm.RefreshAsync();

        var afterCount = vm.Groups[0].Entries.Count;
        Assert.Equal(initialCount + 1, afterCount);
    }
}
