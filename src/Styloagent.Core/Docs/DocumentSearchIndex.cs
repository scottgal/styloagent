using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Styloagent.Core.Docs;

/// <summary>One search result: the matched document's display fields + how to open it.</summary>
public sealed record DocSearchHit(string Title, string FullPath, DocSource Source, string RelativePath);

/// <summary>
/// A small in-memory Lucene index over the document library, backing the top-bar search box. Indexes
/// each doc's title + content and searches with per-term <em>prefix</em> queries so it works
/// as-you-type (autosuggest), with the title boosted. Rebuildable; safe to search after <see cref="Build"/>.
/// </summary>
public sealed class DocumentSearchIndex : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private readonly StandardAnalyzer _analyzer = new(Version);
    private RAMDirectory _dir = new();
    private DirectoryReader? _reader;

    /// <summary>Rebuilds the index from the given documents (title + content). Replaces any prior index.</summary>
    public void Build(IEnumerable<(DocEntry Entry, string Content)> docs)
    {
        var dir = new RAMDirectory();
        var config = new IndexWriterConfig(Version, _analyzer);
        using (var writer = new IndexWriter(dir, config))
        {
            foreach (var (entry, content) in docs)
            {
                writer.AddDocument(new Document
                {
                    new TextField("title", entry.Title ?? "", Field.Store.YES),
                    new TextField("content", content ?? "", Field.Store.NO),
                    new StringField("path", entry.FullPath, Field.Store.YES),
                    new StringField("rel", entry.RelativePath, Field.Store.YES),
                    new StringField("source", entry.Source.ToString(), Field.Store.YES),
                });
            }
            writer.Commit();
        }

        var oldReader = _reader;
        var oldDir = _dir;
        _reader = DirectoryReader.Open(dir);
        _dir = dir;
        oldReader?.Dispose();
        if (!ReferenceEquals(oldDir, dir)) oldDir?.Dispose();
    }

    /// <summary>Top <paramref name="max"/> hits for <paramref name="query"/> — per-term prefix match, title-boosted.</summary>
    public IReadOnlyList<DocSearchHit> Search(string query, int max = 8)
    {
        if (_reader is null || string.IsNullOrWhiteSpace(query)) return Array.Empty<DocSearchHit>();

        var terms = query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(8)
            .ToList();
        if (terms.Count == 0) return Array.Empty<DocSearchHit>();

        // Each term must match (as a prefix) in the title OR the content; the title match is boosted.
        var outer = new BooleanQuery();
        foreach (var term in terms)
        {
            var per = new BooleanQuery
            {
                { new PrefixQuery(new Term("title", term)) { Boost = 3f }, Occur.SHOULD },
                { new PrefixQuery(new Term("content", term)), Occur.SHOULD },
            };
            outer.Add(per, Occur.MUST);
        }

        var searcher = new IndexSearcher(_reader);
        var results = new List<DocSearchHit>();
        foreach (var h in searcher.Search(outer, max).ScoreDocs)
        {
            var d = searcher.Doc(h.Doc);
            var source = Enum.TryParse<DocSource>(d.Get("source"), out var s) ? s : DocSource.Repo;
            results.Add(new DocSearchHit(d.Get("title") ?? "", d.Get("path") ?? "", source, d.Get("rel") ?? ""));
        }
        return results;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _dir?.Dispose();
        _analyzer.Dispose();
    }
}
