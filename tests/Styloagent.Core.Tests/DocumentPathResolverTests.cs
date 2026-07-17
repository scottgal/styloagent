using Styloagent.Core.Attention;

namespace Styloagent.Core.Tests;

/// <summary>
/// open_document path safety: an agent surfacing "this doc" hands an ABSOLUTE or repo-relative path. The
/// resolver canonicalizes it (collapsing <c>..</c>), scopes it to within an open repo root, and confirms it
/// exists — never throwing, returning a rejection reason instead (graceful-degrade posture). Relative paths
/// resolve against the sender's own repo root (the doc the agent naturally holds).
/// </summary>
public class DocumentPathResolverTests
{
    private static readonly string[] OneRoot = { "/x" };

    private static string TempRoot(out string docPath, string docName = "doc.md")
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-opendoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        docPath = Path.Combine(root, docName);
        File.WriteAllText(docPath, "# hi");
        return root;
    }

    [Fact]
    public void Absolute_path_inside_an_open_root_resolves()
    {
        var root = TempRoot(out var doc);
        try
        {
            var r = DocumentPathResolver.Resolve(doc, senderRepoRoot: root, new[] { root });

            Assert.True(r.Ok);
            Assert.Equal(Path.GetFullPath(doc), r.Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Relative_path_resolves_against_the_senders_repo_root()
    {
        var root = TempRoot(out _);
        try
        {
            var r = DocumentPathResolver.Resolve("doc.md", senderRepoRoot: root, new[] { root });

            Assert.True(r.Ok);
            Assert.Equal(Path.Combine(root, "doc.md"), r.Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Traversal_escaping_the_root_is_rejected()
    {
        var root = TempRoot(out _);
        try
        {
            // ../<sibling> climbs out of the open root — must reject even though the raw string names a real dir.
            var r = DocumentPathResolver.Resolve("../etc/passwd", senderRepoRoot: root, new[] { root });

            Assert.False(r.Ok);
            Assert.Null(r.Path);
            Assert.Contains("outside", r.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_path_outside_every_open_root_is_rejected()
    {
        var root = TempRoot(out _);
        try
        {
            var r = DocumentPathResolver.Resolve("/etc/hosts", senderRepoRoot: root, new[] { root });
            Assert.False(r.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_missing_file_inside_the_root_is_rejected()
    {
        var root = TempRoot(out _);
        try
        {
            var r = DocumentPathResolver.Resolve("nope.md", senderRepoRoot: root, new[] { root });
            Assert.False(r.Ok);
            Assert.Contains("exist", r.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Blank_path_is_rejected()
    {
        var r = DocumentPathResolver.Resolve("   ", senderRepoRoot: "/x", OneRoot);
        Assert.False(r.Ok);
    }

    [Fact]
    public void No_open_roots_degrades_to_a_rejection_not_a_throw()
    {
        var r = DocumentPathResolver.Resolve("/x/doc.md", senderRepoRoot: null, Array.Empty<string>());
        Assert.False(r.Ok);
    }
}
