namespace Styloagent.Core.Docs;

/// <summary>
/// One immediate child of a directory listed by <see cref="DocLibraryReader.ListChildren"/>: a subfolder
/// or a <c>*.md</c> file, distinguished by <see cref="IsDirectory"/>. Carries the cheap-to-obtain name and
/// last-write time so the tree can sort by name or modified date without a second I/O pass.
/// </summary>
public sealed record DocDirectoryEntry(string Name, string FullPath, bool IsDirectory, DateTime LastWriteUtc);

/// <summary>Order for <see cref="DocLibraryReader.ListChildren"/> — folders always group before files.</summary>
public enum DocSortOrder
{
    NameAsc,
    NameDesc,
    ModifiedNewest,
    ModifiedOldest,
}

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
        // .styloagent/logs/.cursors/ holds the agent-log writer's per-agent cursor sidecars
        // (<prefix>.json) — machine state, never documents. Excluded so the log index never pulls it in.
        ".cursors",
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
    /// Lists the immediate children of <paramref name="directoryPath"/> — its subfolders (excluding
    /// build/VCS dirs) and its <c>*.md</c> files, one level only, no recursive walk. Backs the
    /// collapsed-by-default doc tree: each folder's contents load on expand instead of up front, so the
    /// 200+ channel messages and agent logs cost nothing until opened. Tolerant like <see cref="Read"/>:
    /// a null/missing/unreadable path yields an empty list and never throws. Folders always precede files;
    /// within each group the order follows <paramref name="order"/>.
    /// </summary>
    public static IReadOnlyList<DocDirectoryEntry> ListChildren(
        string? directoryPath, DocSortOrder order = DocSortOrder.NameAsc)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return Array.Empty<DocDirectoryEntry>();

        DirectoryInfo dir;
        try
        {
            dir = new DirectoryInfo(directoryPath);
            if (!dir.Exists) return Array.Empty<DocDirectoryEntry>();
        }
        catch { return Array.Empty<DocDirectoryEntry>(); }

        var children = new List<DocDirectoryEntry>();

        // Immediate subfolders, minus build/VCS dirs. DirectoryInfo carries the write time from the
        // enumeration, so sorting by modified date needs no extra stat call.
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (ExcludedDirs.Contains(sub.Name)) continue;
                children.Add(new DocDirectoryEntry(sub.Name, sub.FullName, IsDirectory: true, SafeWriteUtc(sub)));
            }
        }
        catch { /* unreadable dir — yield what we have, never throw */ }

        // Immediate *.md files only (parity with the recursive Read).
        try
        {
            foreach (var file in dir.EnumerateFiles("*.md"))
                children.Add(new DocDirectoryEntry(file.Name, file.FullName, IsDirectory: false, SafeWriteUtc(file)));
        }
        catch { /* unreadable dir — yield what we have, never throw */ }

        return SortChildren(children, order);
    }

    /// <summary>Folders first, then the chosen key within each group; a name tiebreak keeps it deterministic.</summary>
    private static IReadOnlyList<DocDirectoryEntry> SortChildren(List<DocDirectoryEntry> children, DocSortOrder order)
    {
        var foldersFirst = children.OrderByDescending(e => e.IsDirectory);
        return order switch
        {
            DocSortOrder.NameDesc => foldersFirst
                .ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            DocSortOrder.ModifiedNewest => foldersFirst
                .ThenByDescending(e => e.LastWriteUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            DocSortOrder.ModifiedOldest => foldersFirst
                .ThenBy(e => e.LastWriteUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => foldersFirst
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static DateTime SafeWriteUtc(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
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
