using System.Text;

namespace Styloagent.Core.Git;

/// <summary>Derives a worktree checkout path and branch name for an agent prefix (pure).</summary>
public static class WorktreeNaming
{
    public static (string Path, string Branch) For(string repoRoot, string prefix, IEnumerable<string> existingPaths)
    {
        var slug = Slug(prefix);
        var baseDir = Path.Combine(repoRoot, ".worktrees", slug);
        var taken = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);

        var path = baseDir;
        var branch = $"agent/{slug}";
        int n = 1;
        while (taken.Contains(path))
        {
            n++;
            path = $"{baseDir}-{n}";
            branch = $"agent/{slug}-{n}";
        }
        return (path, branch);
    }

    private static string Slug(string prefix)
    {
        var sb = new StringBuilder();
        foreach (var c in prefix.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "agent" : s;
    }
}
