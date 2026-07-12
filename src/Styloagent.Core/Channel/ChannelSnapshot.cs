namespace Styloagent.Core.Channel;

/// <summary>
/// Copies a channel directory tree to a fresh working location, so a live, in-use channel (agents
/// actively reading/writing its inbox/outbox) is never opened or written to directly. Styloagent seeds
/// the roster, delivers messages and writes hook/hydration state into whatever channel it opens — all of
/// which would corrupt a channel another fleet is using. Always open a snapshot, never the original.
/// </summary>
public static class ChannelSnapshot
{
    /// <summary>
    /// Recursively copies <paramref name="sourceChannel"/> into <paramref name="destChannel"/> (created if
    /// absent), rewrites absolute references to the source channel inside the copied markdown to point at the
    /// snapshot, and returns the destination. The source is only read. Paths are matched relatively, so a
    /// source path that recurs elsewhere in the tree can't misdirect a copy.
    ///
    /// The rewrite is a real safety measure, not cosmetics: prototype agents coordinate by RAW file writes to
    /// hardcoded channel paths (e.g. <c>/tmp/agent-channel/outbox/…</c>) baked into their restart prompts and
    /// context docs. Without rewriting, a revived agent following those paths would read — and write — the
    /// LIVE channel. After rewriting, every such path resolves inside the snapshot.
    /// </summary>
    public static string CopyTo(string sourceChannel, string destChannel)
    {
        Directory.CreateDirectory(destChannel);
        foreach (var file in Directory.EnumerateFiles(sourceChannel, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceChannel, file);
            var target = Path.Combine(destChannel, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        RewriteChannelPaths(sourceChannel, destChannel);
        return destChannel;
    }

    /// <summary>Rewrites absolute references to the source channel (and its /tmp↔/private/tmp twin) to the
    /// snapshot, across the copied markdown files. Best-effort per file — a failure never aborts the copy.</summary>
    private static void RewriteChannelPaths(string sourceChannel, string destChannel)
    {
        var froms = PathVariants(sourceChannel);
        foreach (var file in Directory.EnumerateFiles(destChannel, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                var rewritten = text;
                foreach (var from in froms)
                    rewritten = rewritten.Replace(from, destChannel, StringComparison.Ordinal);
                if (!string.Equals(rewritten, text, StringComparison.Ordinal))
                    File.WriteAllText(file, rewritten);
            }
            catch { /* one unreadable/locked file must not abort the snapshot */ }
        }
    }

    /// <summary>The absolute forms a doc may reference the channel by. On macOS <c>/tmp</c> is a symlink to
    /// <c>/private/tmp</c>, so agents write either spelling; rewrite both to the snapshot.</summary>
    private static IReadOnlyList<string> PathVariants(string p)
    {
        var set = new List<string> { p };
        if (p.StartsWith("/private/", StringComparison.Ordinal)) set.Add(p["/private".Length..]);
        else if (p.StartsWith("/tmp/", StringComparison.Ordinal) || p.StartsWith("/var/", StringComparison.Ordinal)) set.Add("/private" + p);
        // Longest first so a variant that is a prefix of another can't partially match.
        set.Sort((a, b) => b.Length.CompareTo(a.Length));
        return set;
    }
}
