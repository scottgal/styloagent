using Styloagent.Core.Docs;
using Xunit;

namespace Styloagent.Core.Tests;

public class DocLibraryReaderTests : IDisposable
{
    private readonly string _repo;
    private readonly string _channel;

    public DocLibraryReaderTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "doclib-repo-" + Guid.NewGuid().ToString("N"));
        _channel = Path.Combine(Path.GetTempPath(), "doclib-chan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        Directory.CreateDirectory(Path.Combine(_repo, "bin"));            // excluded
        Directory.CreateDirectory(Path.Combine(_channel, "saved-context"));

        File.WriteAllText(Path.Combine(_repo, "README.md"), "# readme");
        File.WriteAllText(Path.Combine(_repo, "docs", "design.md"), "# design");
        File.WriteAllText(Path.Combine(_repo, "notes.txt"), "not markdown");   // ignored
        File.WriteAllText(Path.Combine(_repo, "bin", "generated.md"), "# gen"); // excluded dir
        File.WriteAllText(Path.Combine(_channel, "PROTOCOL.md"), "# protocol");
        File.WriteAllText(Path.Combine(_channel, "saved-context", "foss-context.md"), "# ctx");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _repo, _channel })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    [Fact]
    public void Read_finds_repo_and_channel_markdown_grouped_by_source()
    {
        var entries = DocLibraryReader.Read(_repo, _channel);

        Assert.Contains(entries, e => e.Source == DocSource.Repo && e.RelativePath == "README.md");
        Assert.Contains(entries, e => e.Source == DocSource.Repo && e.RelativePath == "docs/design.md");
        Assert.Contains(entries, e => e.Source == DocSource.Channel && e.RelativePath == "PROTOCOL.md");
        Assert.Contains(entries, e => e.Source == DocSource.Channel && e.RelativePath == "saved-context/foss-context.md");
        Assert.Equal("design.md", entries.First(e => e.RelativePath == "docs/design.md").Title);
    }

    [Fact]
    public void Read_excludes_build_dirs_and_non_markdown()
    {
        var entries = DocLibraryReader.Read(_repo, _channel);

        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("bin/"));      // bin excluded
        Assert.DoesNotContain(entries, e => e.Title.EndsWith(".txt"));            // non-md ignored
    }

    [Fact]
    public void Read_is_ordered_repo_first_then_relative_path()
    {
        var entries = DocLibraryReader.Read(_repo, _channel).ToList();
        int firstChannel = entries.FindIndex(e => e.Source == DocSource.Channel);
        int lastRepo = entries.FindLastIndex(e => e.Source == DocSource.Repo);
        Assert.True(lastRepo < firstChannel, "all Repo entries should precede Channel entries");
    }

    [Fact]
    public void Read_tolerates_missing_or_null_roots()
    {
        Assert.Empty(DocLibraryReader.Read(null, null));
        var only = DocLibraryReader.Read("/no/such/path/xyz", _channel);
        Assert.All(only, e => Assert.Equal(DocSource.Channel, e.Source));
    }
}
