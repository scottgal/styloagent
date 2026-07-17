namespace Styloagent.Core.Docs;

/// <summary>
/// Enumerates markdown documents from a repo/worktree root and a channel root into <see cref="DocEntry"/>s.
/// Tolerant by design: unreadable directories are skipped and it never throws — a missing or bad path
/// simply yields fewer entries.
/// </summary>
public static class DocLibraryReader
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", ".idea", ".superpowers",
    };

    /// <summary>
    /// Upper bound on directories scanned in a single read. Far above any normal project's doc tree,
    /// but guards against an unbounded walk when pointed at a pathological root (a huge repo, the home
    /// directory, or the OS temp tree) — without it the scan can run for minutes and appear to hang.
    /// </summary>
    private const int MaxDirectories = 20_000;

    /// <summary>
    /// Reads all <c>*.md</c> under <paramref name="repoRoot"/> (as <see cref="DocSource.Repo"/>),
    /// <paramref name="channelRoot"/> (as <see cref="DocSource.Channel"/>), and the per-agent log
    /// directory (as <see cref="DocSource.Log"/>), excluding build/VCS dirs. The log root is
    /// <paramref name="logsRoot"/> when given, else the <c>logs/</c> sibling of the channel root —
    /// see <see cref="ResolveLogsRoot"/>. Any root may be null/missing. Ordered by source then relative path.
    /// </summary>
    public static IReadOnlyList<DocEntry> Read(string? repoRoot, string? channelRoot, string? logsRoot = null)
    {
        var entries = new List<DocEntry>();
        AddFrom(entries, repoRoot, DocSource.Repo);
        AddFrom(entries, channelRoot, DocSource.Channel);
        AddFrom(entries, ResolveLogsRoot(channelRoot, logsRoot), DocSource.Log);

        return entries
            .OrderBy(e => e.Source)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The per-agent log directory. An explicit <paramref name="logsRoot"/> wins; otherwise it is the
    /// <c>logs/</c> sibling of <paramref name="channelRoot"/> — both live under <c>.styloagent/</c>, so the
    /// logs are indexed wherever the channel lives, including the snapshot case where the channel is copied
    /// out of the repo tree. Null when neither is available.
    /// </summary>
    private static string? ResolveLogsRoot(string? channelRoot, string? logsRoot)
    {
        if (!string.IsNullOrWhiteSpace(logsRoot)) return logsRoot;
        if (string.IsNullOrWhiteSpace(channelRoot)) return null;

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(channelRoot));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "logs");
    }

    private static void AddFrom(List<DocEntry> entries, string? root, DocSource source)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        foreach (string path in SafeEnumerateMarkdown(root))
        {
            string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            entries.Add(new DocEntry(Path.GetFileName(path), path, source, rel));
        }
    }

    /// <summary>
    /// Depth-first walk yielding <c>*.md</c> files, skipping excluded directories and swallowing any
    /// per-directory access errors so one unreadable folder can't abort the whole scan.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateMarkdown(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int visited = 0;

        while (stack.Count > 0)
        {
            if (visited++ >= MaxDirectories)
                yield break;   // bounded: never let a pathological root turn into a multi-minute scan

            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); }
            catch { files = Array.Empty<string>(); }
            foreach (string f in files)
                yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = Array.Empty<string>(); }
            foreach (string sub in subdirs)
            {
                if (!ExcludedDirs.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);
            }
        }
    }
}
