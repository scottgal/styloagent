using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
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
///
/// <para>Finding a file <em>by name</em> is the most common use, so the file name gets a dedicated
/// <c>filename</c> field: it is split on <c>- _ . /</c> and camelCase/digit boundaries, and indexed as
/// the full name, the stem, the separator-free run-together form, and every fragment — so any piece of a
/// hyphen/underscore/dot name matches as-you-type. That field is queried with a high boost so a name hit
/// ranks at/above a title or content hit (see <see cref="FilenameBoost"/>).</para>
/// </summary>
public sealed class DocumentSearchIndex : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>Boost for a filename-field hit — above the title boost (3) so finding a file by name wins.</summary>
    private const float FilenameBoost = 5f;

    private readonly StandardAnalyzer _analyzer = new(Version);

    // The filename field is pre-tokenized in C# into a space-joined token list, so it is indexed with a
    // whitespace (non-splitting) analyzer that keeps each prepared token whole — StandardAnalyzer would
    // re-split "activity-timeline.md" and defeat the point. The wrapper does not own the analyzers it
    // wraps, so both components are held and disposed here.
    private readonly WhitespaceAnalyzer _filenameAnalyzer = new(Version);
    private readonly Analyzer _indexAnalyzer;

    private RAMDirectory _dir = new();
    private DirectoryReader? _reader;

    public DocumentSearchIndex()
    {
        _indexAnalyzer = new PerFieldAnalyzerWrapper(
            _analyzer,
            new Dictionary<string, Analyzer> { ["filename"] = _filenameAnalyzer });
    }

    /// <summary>Rebuilds the index from the given documents (title + content). Replaces any prior index.</summary>
    public void Build(IEnumerable<(DocEntry Entry, string Content)> docs)
    {
        var dir = new RAMDirectory();
        var config = new IndexWriterConfig(Version, _indexAnalyzer);
        using (var writer = new IndexWriter(dir, config))
        {
            foreach (var (entry, content) in docs)
            {
                writer.AddDocument(new Document
                {
                    new TextField("title", entry.Title ?? "", Field.Store.YES),
                    new TextField("filename", BuildFilenameField(entry.Title, entry.RelativePath), Field.Store.NO),
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

        // Each term must match (as a prefix) in the filename, the title, OR the content. The filename
        // match carries the highest boost so finding a file by name ranks above a title/content hit.
        var outer = new BooleanQuery();
        foreach (var term in terms)
        {
            var per = new BooleanQuery
            {
                { new PrefixQuery(new Term("filename", term)) { Boost = FilenameBoost }, Occur.SHOULD },
                { new PrefixQuery(new Term("title", term)) { Boost = 3f }, Occur.SHOULD },
                { new PrefixQuery(new Term("content", term)), Occur.SHOULD },
            };
            outer.Add(per, Occur.MUST);
        }

        return RunQuery(outer, max);
    }

    /// <summary>
    /// Top <paramref name="max"/> hits for a FILENAME search — per-term prefix over the <c>filename</c>
    /// field only (full name, stem, run-together form, and <c>-_./</c>/camelCase fragments), with NO
    /// content matching. Backs the in-pane "find a file by name" box: it surfaces name matches and nothing
    /// else, and is global (finds files anywhere in the library, not just the expanded folders). All terms
    /// must match; empty before <see cref="Build"/> or on a blank query.
    /// </summary>
    public IReadOnlyList<DocSearchHit> SearchByName(string query, int max = 8)
    {
        if (_reader is null || string.IsNullOrWhiteSpace(query)) return Array.Empty<DocSearchHit>();

        var terms = query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(8)
            .ToList();
        if (terms.Count == 0) return Array.Empty<DocSearchHit>();

        // Each term must prefix-match the filename field only — no title/content, so a body mention of
        // the term never surfaces a file whose name doesn't contain it.
        var outer = new BooleanQuery();
        foreach (var term in terms)
            outer.Add(new PrefixQuery(new Term("filename", term)), Occur.MUST);

        return RunQuery(outer, max);
    }

    /// <summary>Runs <paramref name="query"/> and maps the top <paramref name="max"/> scoring docs to hits.</summary>
    private IReadOnlyList<DocSearchHit> RunQuery(Query query, int max)
    {
        if (_reader is null) return Array.Empty<DocSearchHit>();

        var searcher = new IndexSearcher(_reader);
        var results = new List<DocSearchHit>();
        foreach (var h in searcher.Search(query, max).ScoreDocs)
        {
            var d = searcher.Doc(h.Doc);
            var source = Enum.TryParse<DocSource>(d.Get("source"), out var s) ? s : DocSource.Repo;
            results.Add(new DocSearchHit(d.Get("title") ?? "", d.Get("path") ?? "", source, d.Get("rel") ?? ""));
        }
        return results;
    }

    /// <summary>
    /// Builds the space-joined token bag for the <c>filename</c> field from a file name and its relative
    /// path. Tokens (all lowercased, whitespace-free, de-duplicated): the full name, the stem, the
    /// separator-free run-together stem, every fragment from splitting on <c>- _ . /</c> and
    /// camelCase/digit boundaries, and the same fragments from the containing folder segments — so any
    /// piece of a name (or its folder) matches as-you-type. Indexed with a whitespace analyzer, so each
    /// prepared token stays whole.
    /// </summary>
    private static string BuildFilenameField(string? title, string? relativePath)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? t)
        {
            if (!string.IsNullOrEmpty(t)) tokens.Add(t.ToLowerInvariant());
        }

        var name = title ?? "";
        Add(name);                                             // full name, incl. extension
        var stem = Path.GetFileNameWithoutExtension(name);
        Add(stem);                                             // stem (extension dropped)
        Add(new string(stem.Where(char.IsLetterOrDigit).ToArray()));   // run-together, e.g. "signalbus"

        foreach (var part in Fragments(name)) Add(part);       // per-fragment, incl. the extension token

        // Folder segments of the relative path help locate a file by the directory it lives in.
        var folder = Path.GetDirectoryName((relativePath ?? "").Replace('\\', '/')) ?? "";
        foreach (var part in Fragments(folder)) Add(part);

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Splits a name/path into lowercase fragments on <c>- _ . / \</c> and on lower→upper camelCase /
    /// letter↔digit boundaries (so "activity-timeline.md" → activity, timeline, md; "sessionPane2" →
    /// session, pane, 2). Runs of capitals are kept together (e.g. "APIClient" stays "apiclient").
    /// </summary>
    private static IEnumerable<string> Fragments(string s)
    {
        var sb = new StringBuilder();

        foreach (var ch in s)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }

            if (sb.Length > 0)
            {
                var prev = sb[sb.Length - 1];
                bool boundary =
                    (char.IsLower(prev) && char.IsUpper(ch)) ||   // camelCase: timeLine → time|Line
                    (char.IsLetter(prev) && char.IsDigit(ch)) ||  // letter→digit: v2 → v|2
                    (char.IsDigit(prev) && char.IsLetter(ch));    // digit→letter: 2b → 2|b
                if (boundary) { yield return sb.ToString(); sb.Clear(); }
            }

            sb.Append(ch);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _dir?.Dispose();
        _indexAnalyzer.Dispose();
        _filenameAnalyzer.Dispose();
        _analyzer.Dispose();
    }
}
