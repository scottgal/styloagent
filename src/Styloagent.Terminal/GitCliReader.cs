using System.Diagnostics;
using Styloagent.Core.Git;

namespace Styloagent.Terminal;

/// <summary>
/// <see cref="IGitReader"/> backed by the <c>git</c> CLI (`git worktree list --porcelain`).
/// Never throws: a missing git / non-repo path yields an empty list.
/// </summary>
public sealed class GitCliReader : IGitReader
{
    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repoRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return Array.Empty<GitWorktree>();

        try
        {
            var psi = new ProcessStartInfo(ResolveGit())
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--porcelain");

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<GitWorktree>();

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? Parse(stdout) : Array.Empty<GitWorktree>();
        }
        catch (Exception)
        {
            return Array.Empty<GitWorktree>();
        }
    }

    /// <summary>Parses `git worktree list --porcelain` output into worktrees.</summary>
    public static IReadOnlyList<GitWorktree> Parse(string porcelain)
    {
        var list = new List<GitWorktree>();
        string? path = null;
        string head = "";
        string? branch = null;

        void Flush()
        {
            if (path is not null) list.Add(new GitWorktree(path, branch, head));
            path = null; head = ""; branch = null;
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ")) path = line["worktree ".Length..];
            else if (line.StartsWith("HEAD ")) head = line["HEAD ".Length..];
            else if (line.StartsWith("branch ")) branch = ShortBranch(line["branch ".Length..]);
            else if (line == "detached") branch = null;
        }
        Flush();
        return list;
    }

    private static string ShortBranch(string refName)
        => refName.StartsWith("refs/heads/") ? refName["refs/heads/".Length..] : refName;

    // Finder-launched .apps don't inherit the login PATH; resolve git explicitly.
    private static string ResolveGit()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;
        return "git";
    }
}
