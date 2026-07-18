using Styloagent.Core.Docs;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// Tests the LAZY per-directory listing API — <see cref="DocLibraryReader.ListChildren"/> — that backs the
/// collapsed-by-default doc tree: one folder's immediate children on expand, not a recursive walk.
/// </summary>
public class DocLibraryListChildrenTests : IDisposable
{
    private readonly string _root;

    public DocLibraryListChildrenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "doclib-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));            // excluded build dir

        File.WriteAllText(Path.Combine(_root, "zeta.md"), "# z");
        File.WriteAllText(Path.Combine(_root, "middle.md"), "# m");
        File.WriteAllText(Path.Combine(_root, "aardvark.md"), "# a");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not markdown");  // ignored
        File.WriteAllText(Path.Combine(_root, "alpha", "deep.md"), "# nested"); // one level down — not immediate

        // Distinct write times so modified-date sort is deterministic.
        File.SetLastWriteTimeUtc(Path.Combine(_root, "aardvark.md"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "middle.md"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "zeta.md"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Mtimes were set so newest→oldest matches reverse-alphabetical, so the same two orderings serve both
    // the name and the modified-date assertions.
    private static readonly string[] ExpectedAToZ = { "aardvark.md", "middle.md", "zeta.md" };
    private static readonly string[] ExpectedZToA = { "zeta.md", "middle.md", "aardvark.md" };

    private static List<string> FileNames(IEnumerable<DocDirectoryEntry> kids)
        => kids.Where(e => !e.IsFolder).Select(e => e.Name).ToList();

    [Fact]
    public void Lists_immediate_subfolders_and_markdown_files_only()
    {
        var kids = DocLibraryReader.ListChildren(_root);

        Assert.Contains(kids, e => e.IsFolder && e.Name == "alpha");
        Assert.Contains(kids, e => e.IsFolder && e.Name == "beta");
        Assert.Contains(kids, e => !e.IsFolder && e.Name == "zeta.md");
        Assert.DoesNotContain(kids, e => e.Name == "bin");        // excluded build dir
        Assert.DoesNotContain(kids, e => e.Name == "notes.txt");  // non-markdown
        Assert.DoesNotContain(kids, e => e.Name == "deep.md");    // nested — not an immediate child
    }

    [Fact]
    public void Lists_the_immediate_children_of_a_subfolder_not_the_parent()
    {
        var kids = DocLibraryReader.ListChildren(Path.Combine(_root, "alpha"));

        Assert.Contains(kids, e => !e.IsFolder && e.Name == "deep.md");
        Assert.DoesNotContain(kids, e => e.Name == "zeta.md");   // that's the parent's file
    }

    [Fact]
    public void Puts_folders_before_files()
    {
        var kids = DocLibraryReader.ListChildren(_root);

        int lastFolder = kids.ToList().FindLastIndex(e => e.IsFolder);
        int firstFile = kids.ToList().FindIndex(e => !e.IsFolder);
        Assert.True(lastFolder < firstFile, "all folders should precede all files");
    }

    [Fact]
    public void Sorts_files_by_name_ascending_by_default()
    {
        var kids = DocLibraryReader.ListChildren(_root);
        Assert.Equal(ExpectedAToZ, FileNames(kids));
    }

    [Fact]
    public void Sorts_files_by_name_descending()
    {
        var kids = DocLibraryReader.ListChildren(_root, DocSortOrder.NameDesc);
        Assert.Equal(ExpectedZToA, FileNames(kids));
    }

    [Fact]
    public void Sorts_files_by_modified_newest_first()
    {
        var kids = DocLibraryReader.ListChildren(_root, DocSortOrder.ModifiedNewest);
        Assert.Equal(ExpectedZToA, FileNames(kids));
    }

    [Fact]
    public void Sorts_files_by_modified_oldest_first()
    {
        var kids = DocLibraryReader.ListChildren(_root, DocSortOrder.ModifiedOldest);
        Assert.Equal(ExpectedAToZ, FileNames(kids));
    }

    [Fact]
    public void Exposes_last_write_time_per_entry()
    {
        var kids = DocLibraryReader.ListChildren(_root);
        var zeta = kids.Single(e => e.Name == "zeta.md");
        Assert.Equal(new DateTimeOffset(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc)), zeta.LastWriteUtc);
    }

    [Fact]
    public void Tolerates_null_missing_and_empty_paths()
    {
        Assert.Empty(DocLibraryReader.ListChildren(null));
        Assert.Empty(DocLibraryReader.ListChildren(""));
        Assert.Empty(DocLibraryReader.ListChildren("/no/such/path/xyz"));
    }
}
