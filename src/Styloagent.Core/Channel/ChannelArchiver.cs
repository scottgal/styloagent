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
    {
        if (string.IsNullOrWhiteSpace(channelRoot) || string.IsNullOrWhiteSpace(threadSlug))
            return 0;

        var slug = threadSlug.Trim().ToLowerInvariant();
        int moved = 0;

        moved += MoveMatching(channelRoot, "inbox", slug);
        moved += MoveMatching(channelRoot, "outbox", slug);

        return moved;
    }

    private static int MoveMatching(string channelRoot, string subdir, string slug)
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

            // Remove routing prefix (anything up to and including the first '-')
            var dashIdx = baseName.IndexOf('-');
            var fileSlug = dashIdx >= 0 ? baseName[(dashIdx + 1)..] : baseName;

            // Also strip follow-up- / redirect- markers
            if (fileSlug.StartsWith("follow-up-", StringComparison.OrdinalIgnoreCase))
                fileSlug = fileSlug["follow-up-".Length..];
            else if (fileSlug.StartsWith("redirect-", StringComparison.OrdinalIgnoreCase))
                fileSlug = fileSlug["redirect-".Length..];

            if (!fileSlug.Equals(slug, StringComparison.OrdinalIgnoreCase))
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
}
