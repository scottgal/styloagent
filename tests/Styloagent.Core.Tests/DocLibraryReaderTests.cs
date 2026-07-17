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

    [Fact]
    public void Read_indexes_agent_logs_alongside_the_channel_as_log_source()
    {
        // Agent logs live in .styloagent/logs/, the sibling of .styloagent/channel/. Read derives that
        // sibling from the channel root so the logs are indexed wherever the channel lives.
        var baseDir = Path.Combine(Path.GetTempPath(), "doclib-logs-" + Guid.NewGuid().ToString("N"));
        var sa = Path.Combine(baseDir, ".styloagent");
        var chan = Path.Combine(sa, "channel");
        var logs = Path.Combine(sa, "logs");
        Directory.CreateDirectory(chan);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(chan, "PROTOCOL.md"), "# protocol");
        File.WriteAllText(Path.Combine(logs, "session-.md"),
            "# Agent log — session-\n\n## 2026-07-17 01:47:30 · assistant\nindexed by lucene\n");
        try
        {
            var entries = DocLibraryReader.Read(repoRoot: null, channelRoot: chan);
            Assert.Contains(entries, e => e.Source == DocSource.Log && e.RelativePath == "session-.md");

            // End-to-end: the log must be discoverable via the existing document search.
            using var idx = new DocumentSearchIndex();
            idx.Build(entries.Select(e => (e, File.ReadAllText(e.FullPath))));
            var hits = idx.Search("lucene");
            Assert.Contains(hits, h => h.FullPath == Path.Combine(logs, "session-.md"));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Read_accepts_an_explicit_logs_root()
    {
        var logs = Path.Combine(Path.GetTempPath(), "doclib-logsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "overview-.md"), "# Agent log — overview-");
        try
        {
            var entries = DocLibraryReader.Read(repoRoot: null, channelRoot: null, logsRoot: logs);
            Assert.Contains(entries, e => e.Source == DocSource.Log && e.RelativePath == "overview-.md");
        }
        finally { Directory.Delete(logs, recursive: true); }
    }
}
