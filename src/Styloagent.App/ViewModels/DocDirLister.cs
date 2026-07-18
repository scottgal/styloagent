namespace Styloagent.App.ViewModels;

/// <summary>One immediate child of a directory in the lazy doc browser: a subfolder or a <c>*.md</c> file,
/// with its last-write time so the tree can sort by modified date.</summary>
public sealed record DocDirItem(string Name, string FullPath, bool IsFolder, DateTimeOffset LastWriteUtc);

/// <summary>
/// Lists ONE directory's immediate doc children (non-excluded subfolders + <c>*.md</c> files) for the lazy
/// browser — no recursive walk, so opening a folder is cheap and "show everything" never does an upfront
/// full-tree scan. The App-side default (<see cref="LocalDocDirLister"/>) swaps 1:1 for repo-'s Core
/// <c>DocLibraryReader.ListChildren</c> when it lands (same name/path/isFolder/mtime shape).
/// </summary>
public interface IDocDirLister
{
    IReadOnlyList<DocDirItem> List(string dir);
}

/// <summary>App-side per-directory lister used until repo-'s Core listing API lands. Mirrors the reader's
/// excluded-dir set; tolerant (missing/unreadable dir → empty, never throws).</summary>
public sealed class LocalDocDirLister : IDocDirLister
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", ".idea", ".superpowers", ".cursors",
    };

    public IReadOnlyList<DocDirItem> List(string dir)
    {
        var items = new List<DocDirItem>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return items;

        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.Length == 0 || Excluded.Contains(name)) continue;
                items.Add(new DocDirItem(name, sub, IsFolder: true, Mtime(sub)));
            }
        }
        catch { /* unreadable dir → just fewer folders */ }

        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.md"))
                items.Add(new DocDirItem(Path.GetFileName(f), f, IsFolder: false, Mtime(f)));
        }
        catch { /* unreadable dir → just fewer files */ }

        return items;
    }

    private static DateTimeOffset Mtime(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch { return DateTimeOffset.MinValue; }
    }
}
