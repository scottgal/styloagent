using SeekStorm.Bindings;
using SeekStorm.Bindings.Models;

namespace Styloagent.Core.Search;

/// <summary>
/// Single SeekStorm-backed search index for all project corpora (memory, docs, bus, issues).
/// Replaces the three separate ranking implementations (MemoryRecallService BM25+embeddings,
/// ContextRetrievalService lexical, DocumentSearchIndex FTS5) with one BM25F-native engine.
/// The ingestion layer (reading files, chunking, parsing) stays unchanged — only the index and
/// search calls change.
/// </summary>
public sealed class UnifiedSearchService : IDisposable
{
    private SeekStormClient _client = new();
    private readonly string _indexPath;
    private bool _opened;

    private static readonly SchemaField[] Schema =
    [
        new() { Field = "source", FieldType = "String16", Store = true, IndexLexical = true },
        new() { Field = "title", FieldType = "Text", Store = true, IndexLexical = true, Boost = 8 },
        new() { Field = "path", FieldType = "String32", Store = true, IndexLexical = true },
        new() { Field = "state", FieldType = "String16", Store = true },
        new() { Field = "content", FieldType = "Text", Store = true, IndexLexical = true, Longest = true },
        new() { Field = "salience", FieldType = "F64", Store = true },
        new() { Field = "freshness", FieldType = "I64", Store = true },
    ];

    public UnifiedSearchService(string indexPath)
    {
        _indexPath = indexPath;
    }

    /// <summary>Creates or opens the index. Call once before indexing or searching.</summary>
    public void Open()
    {
        if (_opened) return;
        var dir = Path.GetDirectoryName(_indexPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (Directory.Exists(_indexPath) && Directory.EnumerateFileSystemEntries(_indexPath).Any())
            _client.OpenIndex(_indexPath);
        else
            _client.CreateIndex(_indexPath, new IndexMeta
            {
                Name = "styloagent",
                LexicalSimilarity = "Bm25f",
                Tokenizer = "UnicodeAlphanumeric",
                Stemmer = "None",
                StopWords = "English",
            }, Schema);

        _opened = true;
    }

    /// <summary>
    /// Replaces the entire corpus atomically: deletes the old index, creates a fresh one,
    /// and indexes all documents in batches.
    /// </summary>
    public void Rebuild(IEnumerable<SearchDocument> documents)
    {
        _client.Dispose();
        try { if (Directory.Exists(_indexPath)) Directory.Delete(_indexPath, recursive: true); }
        catch { /* best-effort */ }

        _client = new SeekStormClient();
        _opened = false;
        Open();

        var batch = new List<string>();
        foreach (var doc in documents)
        {
            batch.Add(doc.ToJson());
            if (batch.Count >= 500)
            {
                _client.IndexDocuments($"[{string.Join(",", batch)}]");
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            _client.IndexDocuments($"[{string.Join(",", batch)}]");

        _client.Commit();
    }

    /// <summary>Search with BM25F ranking.</summary>
    public SearchResult Search(string query, int offset = 0, int limit = 10)
        => _client.Search(new SearchRequest
        {
            Query = query,
            Offset = offset,
            Length = limit,
            Realtime = true,
        });

    /// <summary>Retrieve a stored document's fields by ID.</summary>
    public string GetDocument(ulong docId) => _client.GetDocument((nuint)docId);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// A document to be indexed in the unified search service. Mirrors the schema fields.
/// </summary>
public sealed record SearchDocument(
    string Source,
    string Title,
    string Path,
    string State,
    string Content,
    double Salience,
    long Freshness)
{
    public string ToJson()
    {
        var escapedContent = System.Text.Json.JsonSerializer.Serialize(Content);
        var escapedTitle = System.Text.Json.JsonSerializer.Serialize(Title);
        var escapedPath = System.Text.Json.JsonSerializer.Serialize(Path);
        var escapedSource = System.Text.Json.JsonSerializer.Serialize(Source);
        var escapedState = System.Text.Json.JsonSerializer.Serialize(State);
        return $$"""
        {"source":{{escapedSource}},"title":{{escapedTitle}},"path":{{escapedPath}},"state":{{escapedState}},"content":{{escapedContent}},"salience":{{Salience}},"freshness":{{Freshness}}}
        """;
    }
}
