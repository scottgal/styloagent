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

    /// <summary>The channel's own subdirectories/files a doc addresses by absolute path. Any absolute path
    /// ending in one of these tails IS a channel reference, whatever prefix precedes it — that is what lets
    /// the rewrite re-root references regardless of the original channel path.</summary>
    private static readonly string[] ChannelTails =
        { "/saved-context/", "/inbox/", "/outbox/", "/launch-prompts/", "/archive/", "/PROTOCOL.md" };

    /// <summary>
    /// Rewrites absolute references to the channel — in the copied markdown — to point at the snapshot.
    /// Two passes: (1) the exact source path (and its <c>/tmp↔/private/tmp</c> twin); (2) a structural
    /// re-root of any absolute path ending in a channel tail (e.g. <c>…/saved-context/</c>), so a doc that
    /// hardcodes a DIFFERENT original path than the one we opened (renamed/moved/symlinked channel) is still
    /// rerooted to the snapshot rather than leaking to the original. Best-effort per file.
    /// </summary>
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
                foreach (var tail in ChannelTails)
                    rewritten = RerootTail(rewritten, tail, destChannel);
                if (!string.Equals(rewritten, text, StringComparison.Ordinal))
                    File.WriteAllText(file, rewritten);
            }
            catch { /* one unreadable/locked file must not abort the snapshot */ }
        }
    }

    /// <summary>
    /// Re-roots every absolute path ending in <paramref name="tail"/> onto <paramref name="dest"/>: for each
    /// occurrence, scans left from the tail to the start of the absolute path (a delimiter or line start) and
    /// replaces that prefix with <paramref name="dest"/>. Re-rooting an already-snapshot path is a no-op.
    /// </summary>
    private static string RerootTail(string text, string tail, string dest)
    {
        int search = 0;
        while (true)
        {
            int at = text.IndexOf(tail, search, StringComparison.Ordinal);
            if (at < 0) return text;

            // Walk back to the start of the absolute path (the '/' after a delimiter).
            int i = at;
            while (i > 0 && !IsPathDelimiter(text[i - 1])) i--;
            if (text[i] != '/')   // not actually an absolute path — skip this occurrence
            {
                search = at + tail.Length;
                continue;
            }

            var replacement = dest + tail;
            text = text[..i] + replacement + text[(at + tail.Length)..];
            search = i + replacement.Length;
        }
    }

    // A path token ends where markdown/prose would break it: whitespace, quotes, backticks, or brackets.
    private static bool IsPathDelimiter(char c)
        => char.IsWhiteSpace(c) || c is '`' or '"' or '\'' or '(' or ')' or '<' or '>' or '[' or ']';

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
