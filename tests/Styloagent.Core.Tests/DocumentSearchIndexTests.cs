using Styloagent.Core.Docs;

namespace Styloagent.Core.Tests;

public class DocumentSearchIndexTests
{
    private static DocEntry Entry(string title, string rel)
        => new DocEntry(title, "/docs/" + rel, DocSource.Repo, rel);

    private static (DocEntry, string) Doc(string title, string rel, string content)
        => (Entry(title, rel), content);

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
    public void Finds_file_by_hyphenated_name_fragment_typed_with_the_hyphen()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("activity-timeline.md", "activity-timeline.md", "no relevant words in the body"),
            Doc("readme.md", "readme.md", "unrelated content here"),
        });

        // The user types the hyphenated name — StandardAnalyzer tokenizes the title to
        // ["activity","timeline.md"], so a whole-name prefix never matched before.
        Assert.Contains(idx.Search("activity-timeline"), h => h.Title == "activity-timeline.md");
    }

    [Fact]
    public void Finds_file_when_name_typed_as_one_word_without_separators()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("signal-bus.md", "signal-bus.md", "unrelated body"),
            Doc("readme.md", "readme.md", "nothing to see"),
        });

        // "signalbus" — the name run together, no hyphen. A very common way to search by name.
        Assert.Contains(idx.Search("signalbus"), h => h.Title == "signal-bus.md");
    }

    [Fact]
    public void Finds_file_by_underscore_separated_mid_name_fragment()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("my_deploy_guide.md", "my_deploy_guide.md", "unrelated body"),
            Doc("readme.md", "readme.md", "nothing to see"),
        });

        // A middle segment of an underscore-separated name.
        Assert.Contains(idx.Search("guide"), h => h.Title == "my_deploy_guide.md");
    }

    [Fact]
    public void Filename_match_outranks_a_content_only_match()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("other.md", "other.md", "the body says signalbus and signalbus again"),
            Doc("signal-bus.md", "signal-bus.md", "no relevant words in the body"),
        });

        // Finding a file by its name is the primary use — a name hit must beat a body-only mention.
        var hits = idx.Search("signalbus");
        Assert.Equal("signal-bus.md", hits[0].Title);
    }

    [Fact]
    public void SearchByName_finds_a_file_by_a_hyphen_name_fragment()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            Doc("activity-timeline.md", "activity-timeline.md", "unrelated body"),
            Doc("readme.md", "readme.md", "nothing to see"),
        });

        Assert.Contains(idx.SearchByName("timeline"), h => h.Title == "activity-timeline.md");
        Assert.Contains(idx.SearchByName("activity-timeline"), h => h.Title == "activity-timeline.md");
    }

    [Fact]
    public void SearchByName_ignores_content_only_matches()
    {
        using var idx = new DocumentSearchIndex();
        idx.Build(new[]
        {
            // "runtime" appears only in the BODY here — a name search must NOT surface it.
            Doc("notes.md", "notes.md", "this body mentions runtime repeatedly runtime runtime"),
            Doc("runtime.md", "runtime.md", "unrelated body"),
        });

        var hits = idx.SearchByName("runtime");
        Assert.Contains(hits, h => h.Title == "runtime.md");
        Assert.DoesNotContain(hits, h => h.Title == "notes.md");
    }

    [Fact]
    public void BuildNames_makes_names_searchable_without_reading_content()
    {
        using var idx = new DocumentSearchIndex();
        // No content supplied — a names-first pass indexes filename/title only, so it's instant and the
        // "find by name" box works before the (slow) content read has happened.
        idx.BuildNames(new[] { Entry("activity-timeline.md", "activity-timeline.md") });

        Assert.Contains(idx.SearchByName("timeline"), h => h.Title == "activity-timeline.md");
    }

    [Fact]
    public void BuildNames_then_Build_streams_in_the_content_search()
    {
        using var idx = new DocumentSearchIndex();
        var entry = Entry("notes.md", "notes.md");

        idx.BuildNames(new[] { entry });
        Assert.Empty(idx.Search("streamedbody"));   // body not indexed yet — names-only pass

        idx.Build(new[] { (entry, "streamedbody appears here") });
        Assert.Contains(idx.Search("streamedbody"), h => h.Title == "notes.md");   // content now searchable
    }

    [Fact]
    public void SearchByName_before_build_or_on_blank_is_empty()
    {
        using var idx = new DocumentSearchIndex();
        Assert.Empty(idx.SearchByName("anything"));   // before Build
        idx.Build(new[] { Doc("a.md", "a.md", "x") });
        Assert.Empty(idx.SearchByName(""));
        Assert.Empty(idx.SearchByName("   "));
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
