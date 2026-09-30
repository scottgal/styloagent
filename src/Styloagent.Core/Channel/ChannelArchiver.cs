namespace Styloagent.Core.Channel;

/// <summary>
/// Moves completed or operator-archived bus thread files from the live <c>inbox/</c> / <c>outbox/</c>
/// into <c>archive/inbox/</c> / <c>archive/outbox/</c>, so the channel stays glanceable and
/// <see cref="ChannelProjection"/> naturally classifies them as Archived on the next read.
/// Best-effort: a move failure is traced and never throws — the thread stays live rather than vanishing.
/// </summary>
public static class ChannelArchiver
{
    /// <summary>
    /// Archives every file belonging to a thread identified by <paramref name="threadSlug"/>:
    /// the inbox message(s), any follow-ups, and the reply (if present). Matches on the slug
    /// portion of the filename regardless of routing prefix.
    /// Returns the count of files moved.
    /// </summary>
    public static int ArchiveThread(string channelRoot, string threadSlug)
        => ArchiveThreads(channelRoot, [threadSlug]);

    /// <summary>
    /// Archives several threads in one pass over each live directory. A bus refresh can contain thousands
    /// of already-archived threads; scanning inbox/outbox once per thread turns a small refresh into an
    /// O(threads × files) CPU spike. This method keeps it O(files × filename length).
    /// </summary>
    public static int ArchiveThreads(string channelRoot, IEnumerable<string> threadSlugs)
    {
        if (string.IsNullOrWhiteSpace(channelRoot))
            return 0;

        var slugs = threadSlugs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (slugs.Count == 0)
            return 0;

        int moved = 0;

        moved += MoveMatching(channelRoot, "inbox", slugs);
        moved += MoveMatching(channelRoot, "outbox", slugs);

        return moved;
    }

    private static int MoveMatching(string channelRoot, string subdir, IReadOnlySet<string> slugs)
    {
        var sourceDir = Path.Combine(channelRoot, subdir);
        if (!Directory.Exists(sourceDir))
            return 0;

        var archiveDir = Path.Combine(channelRoot, "archive", subdir);
        int moved = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.md"))
        {
            var name = Path.GetFileName(file);
            // Strip known suffixes to get the base slug
            var baseName = name.EndsWith(".reply.md", StringComparison.OrdinalIgnoreCase)
                ? name[..^".reply.md".Length]
                : name[..^".md".Length];

            if (!MatchesAnySlug(baseName, slugs))
                continue;

            try
            {
                Directory.CreateDirectory(archiveDir);
                var dest = Path.Combine(archiveDir, name);
                // If a file with this name already exists in archive, de-dupe with a counter.
                if (File.Exists(dest))
                {
                    int n = 1;
                    while (File.Exists(Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(name)}-{n}.md")))
                        n++;
                    dest = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(name)}-{n}.md");
                }
                File.Move(file, dest);
                moved++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[ChannelArchiver] failed to archive {file}: {ex.Message}");
            }
        }

        return moved;
    }

    private static bool MatchesAnySlug(string baseName, IReadOnlySet<string> slugs)
    {
        if (slugs.Contains(baseName))
            return true;

        // Prefixes themselves may contain dashes (for example agent-12-). Test each dash-delimited suffix
        // against the exact slug set instead of incorrectly assuming the first dash ends the prefix.
        for (int dash = baseName.IndexOf('-'); dash >= 0 && dash + 1 < baseName.Length;
             dash = baseName.IndexOf('-', dash + 1))
        {
            if (slugs.Contains(baseName[(dash + 1)..]))
                return true;
        }

        return false;
    }
}
