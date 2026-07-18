using Styloagent.Core.Docs;

namespace Styloagent.App.ViewModels;

/// <summary>One immediate child of a directory in the lazy doc browser: a subfolder or a <c>*.md</c> file,
/// with its last-write time so the tree can sort by modified date.</summary>
public sealed record DocDirItem(string Name, string FullPath, bool IsFolder, DateTimeOffset LastWriteUtc);

/// <summary>
/// Lists ONE directory's immediate doc children (non-excluded subfolders + <c>*.md</c> files) for the lazy
/// browser — no recursive walk, so opening a folder is cheap and "show everything" never does an upfront
/// full-tree scan. A seam so tests can drive the tree with an in-memory filesystem; production is
/// <see cref="CoreDocDirLister"/> over repo-'s Core listing.
/// </summary>
public interface IDocDirLister
{
    IReadOnlyList<DocDirItem> List(string dir);
}

/// <summary>The production lister: a thin adapter over repo-'s Core <c>DocLibraryReader.ListChildren</c>
/// (aligned to this shape @0fd59d7) — one exclude set, one listing impl. The VM applies its own re-sort on
/// the sort control, so this lists in the default order and maps the Core entry to the App's
/// <see cref="DocDirItem"/> (identical fields: name / full path / is-folder / last-write).</summary>
public sealed class CoreDocDirLister : IDocDirLister
{
    public IReadOnlyList<DocDirItem> List(string dir)
        => DocLibraryReader.ListChildren(dir)
            .Select(e => new DocDirItem(e.Name, e.FullPath, e.IsFolder, e.LastWriteUtc))
            .ToList();
}
