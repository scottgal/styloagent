using Styloagent.Core.Docs;

namespace Styloagent.Core.Tests;

/// <summary>
/// Guards the filename/title search that replaced the removed SQLite FTS5 index. The contract the
/// cockpit relies on: every typed term must match, matching is case-insensitive and substring-based so
/// typing narrows, and a title hit outranks a path-only hit.
/// </summary>
public class DocNameSearchTests
{
    private static readonly DocEntry[] Corpus =
    [
        new("PROTOCOL", "/repo/.styloagent/PROTOCOL.md", DocSource.Repo, ".styloagent/PROTOCOL.md"),
        new("architecture", "/repo/.styloagent/architecture.md", DocSource.Repo, ".styloagent/architecture.md"),
        new("bus-", "/repo/.styloagent/missions/bus-.md", DocSource.Repo, ".styloagent/missions/bus-.md"),
        new("welcome", "/repo/docs/manual/welcome.md", DocSource.Repo, "docs/manual/welcome.md"),
        new("session-", "/logs/session-.md", DocSource.Log, "session-.md"),
    ];

    [Fact]
    public void Search_WithEmptyQuery_ReturnsNothing()
    {
        Assert.Empty(DocNameSearch.Search(Corpus, ""));
        Assert.Empty(DocNameSearch.Search(Corpus, "   "));
    }

    [Fact]
    public void Search_MatchesTitleCaseInsensitively()
    {
        var hits = DocNameSearch.Search(Corpus, "protocol");
        Assert.Equal("/repo/.styloagent/PROTOCOL.md", Assert.Single(hits).FullPath);
    }

    [Fact]
    public void Search_MatchesOnAPartialPrefix_SoTypingNarrows()
    {
        Assert.Contains(DocNameSearch.Search(Corpus, "arch"),
            h => h.FullPath == "/repo/.styloagent/architecture.md");
    }

    [Fact]
    public void Search_RequiresEveryTermToMatch()
    {
        // "manual welcome" — both terms hit the welcome doc (one via path, one via title).
        Assert.Contains(DocNameSearch.Search(Corpus, "manual welcome"),
            h => h.FullPath == "/repo/docs/manual/welcome.md");
        // "manual protocol" — no single document carries both.
        Assert.Empty(DocNameSearch.Search(Corpus, "manual protocol"));
    }

    [Fact]
    public void Search_MatchesOnPathWhenTheTitleDoesNot()
    {
        Assert.Contains(DocNameSearch.Search(Corpus, "missions"),
            h => h.FullPath == "/repo/.styloagent/missions/bus-.md");
    }

    [Fact]
    public void Search_RanksATitleHitAboveAPathOnlyHit()
    {
        var corpus = new[]
        {
            new DocEntry("notes", "/repo/docs/architecture/notes.md", DocSource.Repo, "docs/architecture/notes.md"),
            new DocEntry("architecture", "/repo/docs/architecture.md", DocSource.Repo, "docs/architecture.md"),
        };
        var hits = DocNameSearch.Search(corpus, "architecture");
        Assert.Equal("/repo/docs/architecture.md", hits[0].FullPath);
    }

    [Fact]
    public void Search_HonoursTheResultCap()
    {
        Assert.Single(DocNameSearch.Search(Corpus, "md", max: 1));
    }

    [Fact]
    public void Search_PreservesTheEntrySourceSoTheViewerDispatchesCorrectly()
    {
        var hit = Assert.Single(DocNameSearch.Search(Corpus, "session-"));
        Assert.Equal(DocSource.Log, hit.Source);
        Assert.Equal("session-.md", hit.RelativePath);
    }
}
