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

    [Fact]
    public void Groups_ArePopulated_BySource()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });

        Assert.Equal(2, vm.Groups.Count);
        Assert.Contains(vm.Groups, g => g.Header == "repo");
        Assert.Contains(vm.Groups, g => g.Header == "channel");
    }

    [Fact]
    public void Groups_RepoIsFirst()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });

        Assert.Equal("repo", vm.Groups[0].Header);
        Assert.Equal("channel", vm.Groups[1].Header);
    }

    [Fact]
    public void OpenDoc_InvokesCallback_WithCorrectViewModel()
    {
        MarkdownDocumentViewModel? received = null;
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, docVm => received = docVm);

        var repoGroup = vm.Groups[0];
        Assert.True(repoGroup.Entries.Count > 0);

        var entry = repoGroup.Entries[0];
        vm.OpenDocCommand.Execute(entry);

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
    public void Refresh_RebuildsGroups()
    {
        var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
        var initialCount = vm.Groups[0].Entries.Count;

        File.WriteAllText(Path.Combine(_repoRoot, "extra.md"), "# Extra");

        vm.RefreshCommand.Execute(null);

        var afterCount = vm.Groups[0].Entries.Count;
        Assert.Equal(initialCount + 1, afterCount);
    }
}
