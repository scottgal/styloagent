using Styloagent.Core.Docs;

namespace Styloagent.Core.Tests;

public class DocumentSearchIndexTests
{
    private static (DocEntry, string) Doc(string title, string rel, string content)
        => (new DocEntry(title, "/docs/" + rel, DocSource.Repo, rel), content);

    [Fact]
    public void Finds_by_content_prefix_as_you_type()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("Deployment Guide", "deploy.md", "how to deploy the service to production"),
            Doc("Architecture", "arch.md", "the C4 model and its components"),
        });

        var hits = idx.Search("deplo");   // prefix, mid-typing
        Assert.Contains(hits, h => h.Title == "Deployment Guide");
    }

    [Fact]
    public void Requires_all_terms_to_match()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("A", "a.md", "alpha beta"),
            Doc("B", "b.md", "alpha gamma"),
        });

        var hits = idx.Search("alpha beta");
        Assert.Single(hits);
        Assert.Equal("A", hits[0].Title);
    }

    [Fact]
    public void Title_match_is_boosted_above_a_body_mention()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("Other doc", "o.md", "this mentions runtime once in the body"),
            Doc("Runtime", "r.md", "unrelated words only here"),
        });

        var hits = idx.Search("runtime");
        Assert.Equal("Runtime", hits[0].Title);   // title (boost 3) outranks the body mention
    }

    [Fact]
    public void Empty_or_whitespace_query_returns_nothing()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[] { Doc("A", "a.md", "x") });
        Assert.Empty(idx.Search(""));
        Assert.Empty(idx.Search("   "));
    }

    [Fact]
    public void Search_before_build_is_empty_not_a_crash()
    {
        using var idx = new DocumentSearchIndex();
        Assert.Empty(idx.Search("anything"));
    }
}
