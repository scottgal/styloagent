using System.Text.RegularExpressions;
using Styloagent.Core.Channel;
using Styloagent.Core.Docs;
using Styloagent.Core.Issues;
using Styloagent.Core.Memory;

namespace Styloagent.Core.Retrieval;

public sealed record ContextHit(string Source, string Title, string Path, string State, string Content, double Score);
public sealed record ContextRetrievalResult(IReadOnlyList<ContextHit> Hits, int Bytes, IReadOnlyDictionary<string, int> Candidates);

/// <summary>
/// Bounded cross-corpus retrieval for coding agents. Each source is represented by a small, citeable unit
/// (a Markdown heading chunk, bus thread, issue, or memory) before LucidRAG-style RRF fuses lexical,
/// salience and freshness rankings. This intentionally excludes live authority/router state: it must be
/// read from its deterministic MCP tools, never a possibly stale index.
/// </summary>
public static class ContextRetrievalService
{
    private const int RrfK = 60;
    private static readonly Regex Words = new(@"[\p{L}\p{N}_-]+", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"(?m)^#{1,4}\s+(.+)$", RegexOptions.Compiled);
    private static readonly string[] DefaultSources = ["memory", "docs", "bus", "issues"];

    public static async Task<ContextRetrievalResult> RetrieveAsync(string projectRoot, string channelRoot, string issuesDir,
        IReadOnlyCollection<string> knownPrefixes, MemoryRagOptions memoryOptions, string caller, string query,
        IReadOnlyCollection<string>? sources = null, int limit = 8, int maxBytes = 6144, CancellationToken ct = default)
    {
        var requested = (sources is { Count: > 0 } ? sources : DefaultSources)
            .Select(s => s.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (requested.Contains("memory"))
        {
            var memory = await MemoryRecallService.RecallAsync(memoryOptions, query, limit: 20, maxBytes: 24 * 1024, ct: ct);
            candidates.AddRange(memory.Hits.Select(h => new Candidate("memory", h.Name, h.Path, h.Pinned ? "pinned" : h.Type,
                h.Description + "\n" + h.Content, h.Pinned ? 4 : 1, FileStamp(h.Path))));
            counts["memory"] = memory.CorpusCount;
        }
        if (requested.Contains("docs"))
        {
            var docs = DocLibraryReader.Read(projectRoot, null).Where(d => d.Source == DocSource.Repo);
            foreach (var doc in docs)
                candidates.AddRange(ChunkDocument(doc));
            counts["docs"] = candidates.Count(c => c.Source == "docs");
        }
        if (requested.Contains("bus"))
        {
            var threads = await new ChannelProjection().ReadAsync(channelRoot, knownPrefixes, ct);
            foreach (var thread in threads)
            {
                var view = BusThreadClassifier.Classify(thread);
                if (view.Section == BusThreadSection.Archive) continue; // historical bus is explicit-only later
                var messages = thread.Messages.OrderByDescending(m => m.Timestamp).Take(2).Reverse().ToList();
                var text = string.Join("\n\n", messages.Select(m => m.Body));
                var addressed = thread.Prefixes.Any(p => p.Equals(caller, StringComparison.OrdinalIgnoreCase));
                var urgent = thread.Messages.Any(m => m.Priority == MessagePriority.Urgent);
                var salience = (view.Section == BusThreadSection.Attention ? 4 : 1) + (addressed ? 2 : 0) + (urgent ? 2 : 0);
                candidates.Add(new Candidate("bus", view.Subject, thread.Slug, view.Section.ToString().ToLowerInvariant(), text, salience,
                    thread.Messages.Max(m => m.Timestamp?.UtcTicks ?? 0)));
            }
            counts["bus"] = candidates.Count(c => c.Source == "bus");
        }
        if (requested.Contains("issues"))
        {
            foreach (var issue in IssueStore.Read(issuesDir).Where(i => i.Status.Equals("open", StringComparison.OrdinalIgnoreCase)))
            {
                var salience = issue.Severity.Equals("high", StringComparison.OrdinalIgnoreCase) ? 3 : issue.Severity.Equals("medium", StringComparison.OrdinalIgnoreCase) ? 1.5 : 1;
                candidates.Add(new Candidate("issues", issue.Title, issue.Id, issue.Severity, issue.Detail, salience, issue.Timestamp.UtcTicks));
            }
            counts["issues"] = candidates.Count(c => c.Source == "issues");
        }

        var terms = Tokens(query).ToArray();
        var fused = Fuse(candidates, terms);
        var chosen = new List<ContextHit>();
        var bytes = 0;
        foreach (var item in fused.OrderByDescending(x => x.Value).Select(x => x.Key).Take(Math.Clamp(limit, 1, 20)))
        {
            var content = Trim(item.Content, 950);
            var hit = new ContextHit(item.Source, item.Title, item.Path, item.State, content, fused[item]);
            var size = System.Text.Encoding.UTF8.GetByteCount(hit.Title + hit.Content);
            if (chosen.Count > 0 && bytes + size > Math.Clamp(maxBytes, 1024, 32 * 1024)) continue;
            chosen.Add(hit); bytes += size;
        }
        return new ContextRetrievalResult(chosen, bytes, counts);
    }

    private static Dictionary<Candidate, double> Fuse(IReadOnlyList<Candidate> items, IReadOnlyCollection<string> terms)
    {
        var lexical = items.ToDictionary(i => i, i => Lexical(i, terms));
        var scores = items.ToDictionary(i => i, _ => 0d);
        foreach (var (ranking, weight) in new[] { (lexical, 1d), (items.ToDictionary(i => i, i => i.Salience), .45), (items.ToDictionary(i => i, i => (double)i.Freshness), .2) })
            foreach (var (item, rank) in ranking.OrderByDescending(x => x.Value).Select((x, i) => (x.Key, i))) scores[item] += weight / (RrfK + rank + 1d);
        return scores;
    }

    private static double Lexical(Candidate item, IReadOnlyCollection<string> terms)
        => terms.Sum(t => (item.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ? 3 : 0) + (item.Content.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
    private static IEnumerable<Candidate> ChunkDocument(DocEntry doc)
    {
        string text; try { text = File.ReadAllText(doc.FullPath); } catch { yield break; }
        var headings = Heading.Matches(text).Cast<Match>().ToList();
        if (headings.Count == 0) { yield return new Candidate("docs", doc.Title, doc.FullPath, "document", Trim(text, 1200), 1, FileStamp(doc.FullPath)); yield break; }
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            yield return new Candidate("docs", doc.Title + " · " + headings[i].Groups[1].Value.Trim(), doc.FullPath, "document", Trim(text[start..end], 1200), 1, FileStamp(doc.FullPath));
        }
    }
    private static IEnumerable<string> Tokens(string text) => Words.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(t => t.Length > 1).Distinct();
    private static long FileStamp(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; } }
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
    private sealed record Candidate(string Source, string Title, string Path, string State, string Content, double Salience, long Freshness);
}
