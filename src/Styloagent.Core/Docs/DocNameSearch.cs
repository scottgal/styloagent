namespace Styloagent.Core.Docs;

/// <summary>One search result: the matched document's display fields + how to open it.</summary>
public sealed record DocSearchHit(string Title, string FullPath, DocSource Source, string RelativePath);

/// <summary>
/// Filename/title matching over an already-enumerated document list — the replacement for the removed
/// SQLite FTS5 index. Deliberately a pure function over <see cref="DocEntry"/>s: no database, no
/// persisted index, no document bodies held in memory, nothing to rebuild in the background. The caller
/// owns the entry list (see <see cref="DocLibraryReader.Read"/>), which is names and paths only.
/// </summary>
public static class DocNameSearch
{
    /// <summary>
    /// Ranked filename/title matches for <paramref name="query"/>. Every whitespace-separated term must
    /// appear (prefix-or-substring) in the title or relative path, so typing narrows as you go. Ranking
    /// favours a title-prefix hit, then a title hit, then a path hit, then the shortest path.
    /// </summary>
    public static IReadOnlyList<DocSearchHit> Search(IEnumerable<DocEntry> entries, string query, int max = 8)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8).ToArray();
        if (terms.Length == 0) return [];

        var limit = Math.Clamp(max, 1, 200);
        return entries
            .Select(e => (Entry: e, Rank: Rank(e, terms)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.RelativePath.Length)
            .ThenBy(x => x.Entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new DocSearchHit(x.Entry.Title, x.Entry.FullPath, x.Entry.Source, x.Entry.RelativePath))
            .ToList();
    }

    /// <summary>Lower is better; -1 means "does not match every term" and is filtered out.</summary>
    private static int Rank(DocEntry entry, IReadOnlyList<string> terms)
    {
        var best = int.MaxValue;
        foreach (var term in terms)
        {
            var rank = TermRank(entry, term);
            if (rank < 0) return -1;              // every term must match somewhere
            best = Math.Min(best, rank);          // score on the strongest field any term hit
        }
        return best;
    }

    private static int TermRank(DocEntry entry, string term)
    {
        if (entry.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) return 1;
        if (Path.GetFileName(entry.RelativePath).Contains(term, StringComparison.OrdinalIgnoreCase)) return 2;
        if (entry.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }
}
