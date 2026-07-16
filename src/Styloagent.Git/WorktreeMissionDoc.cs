using System.Diagnostics;

namespace Styloagent.Git;

/// <summary><see cref="WorktreeMissionDoc.PlaceAsync"/>'s result: whether the doc was written, its repo-relative path, and a detail note (e.g. a commit warning).</summary>
public readonly record struct MissionDocResult(bool Ok, string RelativePath, string Detail);

/// <summary>
/// Places an agent's mission doc where the agent can read it from its OWN checkout. A worktree-isolated
/// agent is cut from HEAD by <c>git worktree add</c>, so an uncommitted doc in the main tree is invisible to
/// it. This writes the doc into the target tree at <c>.styloagent/missions/&lt;prefix&gt;.md</c> and — for a
/// worktree — commits it on the agent's branch (force-added, since <c>.styloagent/</c> is gitignored) so it
/// travels with the branch. The write is the load-bearing part (the agent reads the file); the commit is
/// best-effort. Never throws — outcomes surface as a <see cref="MissionDocResult"/>.
/// </summary>
public static class WorktreeMissionDoc
{
    /// <summary>The conventional repo-relative path a mission doc is placed at (forward slashes; git-friendly).</summary>
    public static string RelativePathFor(string prefix) => $".styloagent/missions/{Sanitize(prefix)}.md";

    /// <summary>
    /// Writes <paramref name="content"/> to <c>&lt;treeRoot&gt;/.styloagent/missions/&lt;prefix&gt;.md</c>.
    /// When <paramref name="commit"/> is true (a worktree with its own branch), also force-adds and commits it.
    /// </summary>
    public static async Task<MissionDocResult> PlaceAsync(string treeRoot, string prefix, string content, bool commit, CancellationToken ct = default)
    {
        var rel = RelativePathFor(prefix);
        string abs = Path.Combine(treeRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            await File.WriteAllTextAsync(abs, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(false, rel, $"could not write mission doc: {ex.Message}");
        }

        if (!commit) return new(true, rel, "written (shared tree, not committed)");

        // Force past the .styloagent/ gitignore and commit onto the agent's branch. The worktree is already
        // checked out on agent/<prefix>, so this commit lands only there.
        var add = await RunGitAsync(treeRoot, ct, "add", "-f", "--", rel).ConfigureAwait(false);
        if (!add.Ok) return new(true, rel, $"written but not committed (git add: {add.Stderr.Trim()})");
        var commitRes = await RunGitAsync(treeRoot, ct, "commit", "-m", $"chore(mission): {Sanitize(prefix)} mission doc", "--", rel).ConfigureAwait(false);
        if (!commitRes.Ok) return new(true, rel, $"written but not committed (git commit: {commitRes.Stderr.Trim()})");
        return new(true, rel, "committed");
    }

    private static string Sanitize(string prefix)
    {
        var chars = prefix.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "agent" : s;
    }

    private readonly record struct GitOutcome(bool Ok, string Stdout, string Stderr);

    private static async Task<GitOutcome> RunGitAsync(string workingDir, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveGit())
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return new GitOutcome(false, "", "failed to start git");
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return new GitOutcome(proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new GitOutcome(false, "", ex.Message);
        }
    }

    // Finder-launched .apps don't inherit the login PATH; resolve git explicitly (matches GitService/GitCliReader).
    private static string ResolveGit()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;
        return "git";
    }
}
