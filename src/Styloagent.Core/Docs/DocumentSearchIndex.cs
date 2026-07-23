using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Styloagent.Core.Docs;

/// <summary>One search result: the matched document's display fields + how to open it.</summary>
public sealed record DocSearchHit(string Title, string FullPath, DocSource Source, string RelativePath);

/// <summary>
/// LucidRAG's local SQLite FTS5 document index: a disposable, on-disk corpus that can be rebuilt
/// entirely from the document library. FTS5 provides
/// BM25 ranking; filename tokens and title are weighted above body content for fast file discovery.
/// </summary>
public sealed class DocumentSearchIndex : IDisposable
{
    private static readonly Regex QueryWords = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex NameParts = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _built;

    /// <param name="dbPath">A persistent SQLite file, or null for an in-memory test index.</param>
    public DocumentSearchIndex(string? dbPath = null)
    {
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }
        _db = new SqliteConnection($"Data Source={dbPath ?? ":memory:"};Cache=Shared");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS documents USING fts5(
                path UNINDEXED,
                source UNINDEXED,
                relative_path UNINDEXED,
                filename,
                title,
                content,
                tokenize='unicode61 remove_diacritics 2'
            );
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM documents LIMIT 1)";
        _built = Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Rebuilds the persistent FTS corpus from document title, path and body.</summary>
    public void Build(IEnumerable<(DocEntry Entry, string Content)> docs)
        => ReplaceCorpus(docs.Select(d => (d.Entry, d.Content ?? string.Empty)));

    /// <summary>Rebuilds only filename and title fields, keeping the initial document picker immediate.</summary>
    public void BuildNames(IEnumerable<DocEntry> entries)
        => ReplaceCorpus(entries.Select(e => (e, string.Empty)));

    /// <summary>Returns title/filename/content matches. Each typed word is a prefix and every word must match.</summary>
    public IReadOnlyList<DocSearchHit> Search(string query, int max = 8)
        => SearchCore(query, max, filenameOnly: false);

    /// <summary>Returns filename-only matches for the Document Library's file finder.</summary>
    public IReadOnlyList<DocSearchHit> SearchByName(string query, int max = 8)
        => SearchCore(query, max, filenameOnly: true);

    private void ReplaceCorpus(IEnumerable<(DocEntry Entry, string Content)> docs)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using (var clear = _db.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM documents";
                clear.ExecuteNonQuery();
            }

            using var insert = _db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO documents(path, source, relative_path, filename, title, content) VALUES ($path, $source, $relative, $filename, $title, $content)";
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var source = insert.Parameters.Add("$source", SqliteType.Text);
            var relative = insert.Parameters.Add("$relative", SqliteType.Text);
            var filename = insert.Parameters.Add("$filename", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var content = insert.Parameters.Add("$content", SqliteType.Text);
            foreach (var (entry, body) in docs)
            {
                path.Value = entry.FullPath;
                source.Value = entry.Source.ToString();
                relative.Value = entry.RelativePath;
                filename.Value = FilenameTerms(entry.Title, entry.RelativePath);
                title.Value = entry.Title;
                content.Value = body;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            _built = true;
        }
    }

    private IReadOnlyList<DocSearchHit> SearchCore(string query, int max, bool filenameOnly)
    {
        var fts = ToPrefixQuery(query, filenameOnly);
        if (!_built || string.IsNullOrWhiteSpace(fts)) return [];
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            // The filename/title columns carry substantially more weight than body content. FTS5 bm25
            // returns lower scores first, so retaining ascending rank is intentional.
            cmd.CommandText = filenameOnly
                ? "SELECT path, source, relative_path, title FROM documents WHERE documents MATCH $query ORDER BY bm25(documents, 0, 0, 0, 12, 0, 0) LIMIT $limit"
                : "SELECT path, source, relative_path, title FROM documents WHERE documents MATCH $query ORDER BY bm25(documents, 0, 0, 0, 8, 5, 1) LIMIT $limit";
            cmd.Parameters.AddWithValue("$query", fts);
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(max, 1, 200));
            using var reader = cmd.ExecuteReader();
            var hits = new List<DocSearchHit>();
            while (reader.Read())
            {
                var source = Enum.TryParse<DocSource>(reader.GetString(1), out var parsed) ? parsed : DocSource.Repo;
                hits.Add(new DocSearchHit(reader.GetString(3), reader.GetString(0), source, reader.GetString(2)));
            }
            return hits;
        }
    }

    private static string ToPrefixQuery(string query, bool filenameOnly)
    {
        var terms = QueryWords.Matches(query ?? string.Empty).Select(m => m.Value).Where(x => x.Length > 0).Take(8).ToArray();
        if (terms.Length == 0) return string.Empty;
        var prefixTerms = terms.Select(t => $"{t.Replace("\"", "\"\"")}*");
        var joined = string.Join(" AND ", prefixTerms);
        return filenameOnly ? $"filename : ({joined})" : joined;
    }

    private static string FilenameTerms(string title, string relativePath)
    {
        var seed = $"{title} {relativePath}";
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in NameParts.Matches(seed)) terms.Add(match.Value);
        var compact = new string(seed.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length > 0) terms.Add(compact);
        return string.Join(' ', terms);
    }

    public void Dispose() => _db.Dispose();
}
