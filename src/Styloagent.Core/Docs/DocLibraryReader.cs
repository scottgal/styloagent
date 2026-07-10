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
    /// Reads all <c>*.md</c> under <paramref name="repoRoot"/> (as <see cref="DocSource.Repo"/>) and
    /// <paramref name="channelRoot"/> (as <see cref="DocSource.Channel"/>), excluding build/VCS dirs.
    /// Either root may be null/missing. Results are ordered by source then relative path.
    /// </summary>
    public static IReadOnlyList<DocEntry> Read(string? repoRoot, string? channelRoot)
    {
        var entries = new List<DocEntry>();
        AddFrom(entries, repoRoot, DocSource.Repo);
        AddFrom(entries, channelRoot, DocSource.Channel);

        return entries
            .OrderBy(e => e.Source)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        while (stack.Count > 0)
        {
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
