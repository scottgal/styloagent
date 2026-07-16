using System.Diagnostics;
using System.Formats.Tar;

namespace Styloagent.Git;

/// <summary>The outcome of ensuring the cross-repo LucidView checkout exists under <c>.worktrees/lucidview</c>.</summary>
public enum LucidViewProvisionStatus
{
    /// <summary>The checkout already matched the source HEAD — nothing to do.</summary>
    AlreadyCurrent,
    /// <summary>The checkout was (re)materialised from the source HEAD.</summary>
    Provisioned,
    /// <summary>No sibling lucidview git repo was found to provision from.</summary>
    SourceMissing,
    /// <summary>Provisioning was attempted but failed (git/tar/filesystem error).</summary>
    Failed,
}

/// <summary><see cref="LucidViewProvisioner.EnsureAsync"/>'s result: a status plus a human-readable detail (the HEAD sha on success, else an error).</summary>
public readonly record struct LucidViewProvisionResult(LucidViewProvisionStatus Status, string Detail)
{
    /// <summary>True when the checkout is present and current (already-current or freshly provisioned).</summary>
    public bool Ok => Status is LucidViewProvisionStatus.AlreadyCurrent or LucidViewProvisionStatus.Provisioned;
}

/// <summary>
/// Materialises the cross-repo LucidView dependency into <c>&lt;repo&gt;/.worktrees/lucidview</c> so a
/// git-worktree-isolated agent can build the App. <c>Styloagent.App.csproj</c> references lucidview by the
/// relative path <c>..\..\..\lucidview</c>; from the main tree that resolves to the sibling
/// <c>&lt;repo&gt;/../lucidview</c>, but from a worktree under <c>.worktrees/&lt;agent&gt;/</c> it resolves to
/// <c>.worktrees/lucidview</c>. A <em>symlink</em> there resolves the path but starves the Avalonia XAML
/// compiler (AVLN2000) and shares obj state — only a <em>real</em> checkout works, so we export the source
/// repo's tracked HEAD tree (no <c>.git</c>, no <c>obj</c>/<c>bin</c> — those aren't tracked) into that path.
/// LucidView pulls its markdown renderer from a git <em>submodule</em> (<c>external/LiveMarkdown.Avalonia</c>),
/// which <c>git archive</c> emits only as an empty gitlink; we therefore also archive each initialised
/// submodule's HEAD into its sub-path so the checkout is complete and buildable.
/// Idempotent: a stamp file records the provisioned superproject sha so an unchanged source HEAD (which also
/// pins every submodule) is a fast no-op and a moved HEAD re-provisions. Never throws — failures surface as a
/// <see cref="LucidViewProvisionResult"/>.
/// </summary>
public sealed class LucidViewProvisioner
{
    /// <summary>The shared checkout's directory name under <c>.worktrees/</c> (matches the csproj relative path's leaf).</summary>
    public const string DirName = "lucidview";
    private const string StampFile = ".styloagent-lucidview-head";

    /// <summary>The sibling source repo the checkout is exported from: <c>&lt;repoRoot&gt;/../lucidview</c> (mirrors the csproj's <c>..\..\..\lucidview</c>).</summary>
    public static string SourcePathFor(string repoRoot) => Path.GetFullPath(Path.Combine(repoRoot, "..", DirName));

    /// <summary>The provisioned checkout path: <c>&lt;repoRoot&gt;/.worktrees/lucidview</c> (where a worktree's <c>..\..\..\lucidview</c> resolves).</summary>
    public static string DestPathFor(string repoRoot) => Path.GetFullPath(Path.Combine(repoRoot, ".worktrees", DirName));

    /// <summary>Ensures <c>.worktrees/lucidview</c> is a real checkout pinned to the sibling source repo's HEAD.</summary>
    public static async Task<LucidViewProvisionResult> EnsureAsync(string repoRoot, CancellationToken ct = default)
    {
        var source = SourcePathFor(repoRoot);
        var dest = DestPathFor(repoRoot);

        if (!Directory.Exists(source))
            return new(LucidViewProvisionStatus.SourceMissing, $"no lucidview source at {source}");

        // Pin to the source HEAD; this also validates that the source is a git repo.
        var head = await RunGitAsync(source, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        if (!head.Ok)
            return new(LucidViewProvisionStatus.SourceMissing, $"cannot read HEAD of {source}: {head.Stderr.Trim()}");
        var sha = head.Stdout.Trim();

        var stampPath = Path.Combine(dest, StampFile);
        if (Directory.Exists(dest) && File.Exists(stampPath))
        {
            var stamped = (await File.ReadAllTextAsync(stampPath, ct).ConfigureAwait(false)).Trim();
            if (stamped == sha) return new(LucidViewProvisionStatus.AlreadyCurrent, sha);
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"styloagent-lucidview-{Guid.NewGuid():N}");
        try
        {
            SafeResetDir(dest);

            // 1. Export the superproject's tracked tree at `sha` — gitignored obj/bin and .git are excluded
            //    automatically; submodules land as empty gitlink directories, filled in below.
            var main = await ArchiveExtractAsync(source, sha, dest, tmpDir, ct).ConfigureAwait(false);
            if (!main.Ok) return new(LucidViewProvisionStatus.Failed, main.Error);

            // 2. Fill in each initialised submodule's HEAD (git archive can't recurse into submodules).
            //    `submodule status --recursive` lists parents before children, so extracting in order fills
            //    nested trees progressively; an uninitialised submodule (leading '-') has no checkout to export.
            var subs = await RunGitAsync(source, ct, "submodule", "status", "--recursive").ConfigureAwait(false);
            if (subs.Ok)
                foreach (var relPath in ParseInitialisedSubmodulePaths(subs.Stdout))
                {
                    var subSource = Path.Combine(source, relPath);
                    var subHead = await RunGitAsync(subSource, ct, "rev-parse", "HEAD").ConfigureAwait(false);
                    if (!subHead.Ok) continue;   // not a usable checkout — skip
                    var subDest = Path.Combine(dest, relPath);
                    Directory.CreateDirectory(subDest);
                    var r = await ArchiveExtractAsync(subSource, subHead.Stdout.Trim(), subDest, tmpDir, ct).ConfigureAwait(false);
                    if (!r.Ok) return new(LucidViewProvisionStatus.Failed, $"submodule {relPath}: {r.Error}");
                }

            await File.WriteAllTextAsync(stampPath, sha, ct).ConfigureAwait(false);
            return new(LucidViewProvisionStatus.Provisioned, sha);
        }
        catch (Exception ex)
        {
            return new(LucidViewProvisionStatus.Failed, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Archives <paramref name="treeish"/> from <paramref name="repo"/> and extracts it over <paramref name="destDir"/>.</summary>
    private static async Task<(bool Ok, string Error)> ArchiveExtractAsync(
        string repo, string treeish, string destDir, string tmpDir, CancellationToken ct)
    {
        Directory.CreateDirectory(tmpDir);
        var tar = Path.Combine(tmpDir, $"{Guid.NewGuid():N}.tar");
        var archive = await RunGitAsync(repo, ct, "archive", "--format=tar", "-o", tar, treeish).ConfigureAwait(false);
        if (!archive.Ok) return (false, $"git archive failed: {archive.Stderr.Trim()}");
        try { TarFile.ExtractToDirectory(tar, destDir, overwriteFiles: true); }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(tar); } catch { /* per-tar cleanup is best-effort */ } }
        return (true, "");
    }

    /// <summary>
    /// Parses <c>git submodule status --recursive</c> output to the paths of initialised submodules.
    /// Each line is <c>&lt;flag&gt;&lt;sha&gt; &lt;path&gt; [(&lt;describe&gt;)]</c>; the leading flag is a space
    /// (in sync), <c>+</c> (checked out ≠ recorded), <c>U</c> (conflicts) or <c>-</c> (uninitialised — skipped).
    /// </summary>
    public static IEnumerable<string> ParseInitialisedSubmodulePaths(string statusOutput)
    {
        foreach (var raw in statusOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '-') continue;         // empty or uninitialised
            var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) yield return parts[1];             // parts[0] = sha, parts[1] = path
        }
    }

    /// <summary>Clears <paramref name="dir"/> to an empty real directory. A symlink (the old broken state) is unlinked, never followed into its target.</summary>
    private static void SafeResetDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            var attrs = File.GetAttributes(dir);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir, recursive: false);   // symlink: remove the link only
            else
                Directory.Delete(dir, recursive: true);
        }
        else if (File.Exists(dir))
        {
            File.Delete(dir);                                // a stray symlink-to-file
        }
        Directory.CreateDirectory(dir);
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
