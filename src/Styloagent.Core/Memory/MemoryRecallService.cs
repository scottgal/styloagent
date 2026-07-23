using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Memory;

public sealed record MemoryRecallHit(string Name, string Path, string Type, string Description, string Content, bool Pinned, double Score);
public sealed record MemoryRecallResult(IReadOnlyList<MemoryRecallHit> Hits, bool UsedEmbeddings, int CorpusCount, int Bytes);

/// <summary>
/// Local-first memory retrieval. Markdown files are the source of truth; the JSON vector cache can be
/// discarded at any time. Ranking follows LucidRAG's hybrid RRF shape: independently rank dense, BM25,
/// salience and freshness signals, then fuse them with k=60.
/// </summary>
public sealed class MemoryRecallService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly Regex Word = new(@"[\p{L}\p{N}_-]+", RegexOptions.Compiled);
    private const int RrfK = 60;

    public static async Task<MemoryRecallResult> RecallAsync(MemoryRagOptions options, string query, string? type = null,
        int? limit = null, int? maxBytes = null, CancellationToken ct = default)
    {
        var docs = ReadDocuments(options.Root);
        var filtered = string.IsNullOrWhiteSpace(type) ? docs : docs.Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        var selectedLimit = Math.Clamp(limit ?? options.DefaultLimit, 1, 20);
        var budget = Math.Clamp(maxBytes ?? options.MaxInjectedBytes, 1024, 32 * 1024);
        if (filtered.Count == 0) return new MemoryRecallResult([], false, 0, 0);

        var queryTerms = Tokens(query).ToArray();
        var cache = LoadCache(options.IndexPath);
        var cacheChanged = false;
        float[]? queryEmbedding = null;
        try { queryEmbedding = await EmbedAsync(options, query, ct); } catch { /* lexical retrieval remains fully usable offline */ }

        foreach (var doc in filtered)
        {
            if (queryEmbedding is null) break;
            if (!cache.TryGetValue(doc.Path, out var entry) || entry.MtimeUtcTicks != doc.MtimeUtcTicks || entry.Vector.Length == 0)
            {
                try
                {
                    var vector = await EmbedAsync(options, doc.Name + "\n" + doc.Description + "\n" + doc.Body, ct);
                    if (vector is not null) { cache[doc.Path] = new CachedVector(doc.MtimeUtcTicks, vector); cacheChanged = true; }
                }
                catch { /* leave this document in lexical-only mode */ }
            }
        }
        if (cacheChanged) SaveCache(options.IndexPath, cache);

        var bm25 = Bm25(filtered, queryTerms);
        var dense = queryEmbedding is null ? new Dictionary<MemoryDocument, double>() : filtered
            .Where(d => cache.TryGetValue(d.Path, out var e) && e.Vector.Length == queryEmbedding.Length)
            .ToDictionary(d => d, d => Cosine(queryEmbedding, cache[d.Path].Vector));
        var salience = filtered.ToDictionary(d => d, d => Salience(d, queryTerms));
        var freshness = filtered.ToDictionary(d => d, d => (double)d.MtimeUtcTicks);

        var scores = Fuse(filtered, new (Dictionary<MemoryDocument, double> scores, double weight)[]
        {
            (dense, 1.0), (bm25, 1.0), (salience, 0.3), (freshness, 0.2)
        });
        var pinned = filtered.Where(d => d.Pinned).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var ordered = pinned.Concat(scores.OrderByDescending(x => x.Value).Select(x => x.Key).Where(d => !d.Pinned)).Distinct().Take(selectedLimit);

        var hits = new List<MemoryRecallHit>();
        var used = 0;
        foreach (var doc in ordered)
        {
            var content = Trim(doc.Body, 900);
            var hit = new MemoryRecallHit(doc.Name, doc.Path, doc.Type, doc.Description, content, doc.Pinned, scores.GetValueOrDefault(doc));
            var bytes = System.Text.Encoding.UTF8.GetByteCount(hit.Name + hit.Description + hit.Content);
            if (hits.Count > 0 && used + bytes > budget) continue;
            hits.Add(hit); used += bytes;
        }
        return new MemoryRecallResult(hits, queryEmbedding is not null && dense.Count > 0, filtered.Count, used);
    }

    /// <summary>Proactively refreshes changed/new vectors and removes deleted files from the disposable cache.</summary>
    public static async Task RebuildAsync(MemoryRagOptions options, CancellationToken ct = default)
    {
        var docs = ReadDocuments(options.Root);
        var cache = LoadCache(options.IndexPath);
        var paths = docs.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var changed = cache.Keys.Where(path => !paths.Contains(path)).ToList();
        foreach (var path in changed) cache.Remove(path);
        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue(doc.Path, out var current) && current.MtimeUtcTicks == doc.MtimeUtcTicks && current.Vector.Length > 0) continue;
            try
            {
                var vector = await EmbedAsync(options, doc.Name + "\n" + doc.Description + "\n" + doc.Body, ct);
                if (vector is not null) cache[doc.Path] = new CachedVector(doc.MtimeUtcTicks, vector);
            }
            catch { /* the next file event / retrieval retries; offline BM25 remains valid */ }
        }
        SaveCache(options.IndexPath, cache);
    }

    private static Dictionary<MemoryDocument, double> Bm25(IReadOnlyList<MemoryDocument> docs, IReadOnlyList<string> query)
    {
        var terms = query.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0) return docs.ToDictionary(d => d, _ => 0d);
        var tokenized = docs.ToDictionary(d => d, d => Tokens(d.Name + " " + d.Name + " " + d.Description + " " + d.Body).ToArray());
        var avg = tokenized.Values.Average(x => Math.Max(1, x.Length));
        return docs.ToDictionary(d => d, d => terms.Sum(term =>
        {
            var tf = tokenized[d].Count(t => t.Equals(term, StringComparison.OrdinalIgnoreCase));
            if (tf == 0) return 0d;
            var df = tokenized.Values.Count(tokens => tokens.Any(t => t.Equals(term, StringComparison.OrdinalIgnoreCase)));
            var idf = Math.Log(1 + (docs.Count - df + .5) / (df + .5));
            return idf * (tf * 2.2) / (tf + 1.2 * (1 - .75 + .75 * tokenized[d].Length / avg));
        }));
    }

    private static Dictionary<MemoryDocument, double> Fuse(IReadOnlyList<MemoryDocument> docs,
        IEnumerable<(Dictionary<MemoryDocument, double> scores, double weight)> signals)
    {
        var fused = docs.ToDictionary(d => d, _ => 0d);
        foreach (var (signal, weight) in signals)
            foreach (var (doc, rank) in signal.OrderByDescending(x => x.Value).Select((x, i) => (x.Key, i)))
                fused[doc] += weight / (RrfK + rank + 1d);
        return fused;
    }

    private static double Salience(MemoryDocument d, IReadOnlyCollection<string> query)
        => (d.Pinned ? 2 : 0) + query.Count(t => d.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || d.Description.Contains(t, StringComparison.OrdinalIgnoreCase)) + Math.Min(d.Body.Length / 2000d, .5);

    private static async Task<float[]?> EmbedAsync(MemoryRagOptions options, string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.OllamaEndpoint) || string.IsNullOrWhiteSpace(input)) return null;
        using var response = await Http.PostAsJsonAsync(options.OllamaEndpoint + "/api/embed", new { model = options.EmbeddingModel, input }, ct);
        if (!response.IsSuccessStatusCode) return null;
        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedding>(cancellationToken: ct);
        return payload?.Embeddings?.FirstOrDefault();
    }

    private static List<MemoryDocument> ReadDocuments(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Select(path =>
        {
            var text = File.ReadAllText(path);
            var (frontmatter, body) = SplitFrontmatter(text);
            var name = frontmatter.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(path));
            var description = frontmatter.GetValueOrDefault("description", FirstLine(body));
            var pinned = frontmatter.GetValueOrDefault("pin", "").Equals("true", StringComparison.OrdinalIgnoreCase) || name.Contains('⭐');
            return new MemoryDocument(name, path, frontmatter.GetValueOrDefault("type", "reference"), description, body.Trim(), pinned, File.GetLastWriteTimeUtc(path).Ticks);
        }).ToList();
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---", StringComparison.Ordinal)) return (result, text);
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (result, text);
        foreach (var line in text[3..end].Split('\n'))
        {
            var split = line.IndexOf(':');
            if (split > 0) result[line[..split].Trim()] = line[(split + 1)..].Trim().Trim('"', '\'');
        }
        return (result, text[(end + 4)..]);
    }

    private static IEnumerable<string> Tokens(string text) => Word.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(x => x.Length > 1);
    private static string FirstLine(string text) => text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
    private static double Cosine(float[] a, float[] b) { double dot = 0, aa = 0, bb = 0; for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; } return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb); }
    private static Dictionary<string, CachedVector> LoadCache(string path) { try { return JsonSerializer.Deserialize<Dictionary<string, CachedVector>>(File.ReadAllText(path)) ?? new(); } catch { return new(); } }
    private static void SaveCache(string path, Dictionary<string, CachedVector> cache) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(cache)); }
    private sealed record MemoryDocument(string Name, string Path, string Type, string Description, string Body, bool Pinned, long MtimeUtcTicks);
    private sealed record CachedVector(long MtimeUtcTicks, float[] Vector);
    private sealed record OllamaEmbedding(float[][]? Embeddings);
}
