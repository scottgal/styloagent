using System.Diagnostics;
using Styloagent.Core.Git;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.Git;

/// <summary>
/// <see cref="IGitService"/> and <see cref="IGitLog"/> backed by the <c>git</c> CLI.
/// Never throws: failures surface as a failed <see cref="GitResult"/> carrying git's stderr.
/// Mirrors GitCliReader's process pattern.
/// </summary>
public sealed class GitService : IGitService, IGitLog, IGitDiff, IGitWrite, IGitBranch, IGitStash
{
    /// <summary>
    /// Test seam for the cockpit-freeze regression: a caller sets this to a box before invoking a git
    /// command and <see cref="RunAsync"/> records the managed-thread id the git fork actually ran on.
    /// It flows per logical call (<see cref="AsyncLocal{T}"/>), so parallel tests never corrupt it. The
    /// invariant it guards: the fork must NOT run on the caller (UI/dispatcher) thread.
    /// </summary>
    public static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> ForkThreadProbe = new();

    public async Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ct, "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        return r.Ok ? GitResult<GitStatus>.Success(GitStatusParser.Parse(r.Stdout)) : GitResult<GitStatus>.Fail(r.Stderr);
    }

    public async Task<string?> ResolveRepoRootAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        var r = await RunAsync(path, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!r.Ok) return null;
        var root = r.Stdout.Trim();
        return root.Length == 0 ? null : root;
    }

    public async Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "worktree", "add", worktreePath, "-b", newBranch).ConfigureAwait(false));

    public async Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "worktree", "remove", "--force", worktreePath).ConfigureAwait(false));

    public async Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default)
    {
        var checkout = await RunAsync(repoRoot, ct, "checkout", intoBranch).ConfigureAwait(false);
        if (!checkout.Ok) return GitResult.Fail(checkout.Stderr);
        return ToResult(await RunAsync(repoRoot, ct, "merge", "--no-ff", "--no-edit", sourceBranch).ConfigureAwait(false));
    }

    public async Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "merge", "--abort").ConfigureAwait(false));

    public async Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default)
        => ToResult(await RunAsync(repoRoot, ct, "branch", force ? "-D" : "-d", branch).ConfigureAwait(false));

    public async Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ct,
            "log",
            $"-{limit}",
            "--date-order",
            "--no-show-signature",
            "--decorate=full",
            "--format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s").ConfigureAwait(false);
        return r.Ok
            ? GitResult<IReadOnlyList<Commit>>.Success(CommitLogParser.Parse(r.Stdout))
            : GitResult<IReadOnlyList<Commit>>.Fail(r.Stderr);
    }

    public async Task<GitResult<FileDiff>> GetDiffAsync(string worktreePath, string relativePath, bool staged, CancellationToken ct = default)
    {
        var args = staged
            ? new[] { "diff", "--staged", "--no-color", "--", relativePath }
            : new[] { "diff", "--no-color", "--", relativePath };
        var r = await RunAsync(worktreePath, ct, args).ConfigureAwait(false);
        return r.Ok
            ? GitResult<FileDiff>.Success(UnifiedDiffParser.Parse(relativePath, r.Stdout))
            : GitResult<FileDiff>.Fail(r.Stderr);
    }

    // IGitWrite — Stage/Unstage/Commit/Push/Pull
    // Message is passed as a single argv element so multiline and special-char messages are safe without temp files.
    public async Task<GitResult> StageAsync(string worktreePath, string relativePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "add", "--", relativePath).ConfigureAwait(false));

    public async Task<GitResult> UnstageAsync(string worktreePath, string relativePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "restore", "--staged", "--", relativePath).ConfigureAwait(false));

    public async Task<GitResult> CommitAsync(string worktreePath, string message, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "commit", "-m", message).ConfigureAwait(false));

    public async Task<GitResult> PushAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "push").ConfigureAwait(false));

    public async Task<GitResult> PullAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "pull", "--no-edit").ConfigureAwait(false));

    // IGitBranch — List / Create / Switch
    public async Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string worktreePath, CancellationToken ct = default)
    {
        var current = await RunAsync(worktreePath, ct, "branch", "--show-current").ConfigureAwait(false);
        if (!current.Ok) return GitResult<IReadOnlyList<GitBranch>>.Fail(current.Stderr);
        var currentName = current.Stdout.Trim();

        var r = await RunAsync(worktreePath, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads").ConfigureAwait(false);
        if (!r.Ok) return GitResult<IReadOnlyList<GitBranch>>.Fail(r.Stderr);

        var branches = new List<GitBranch>();
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            branches.Add(new GitBranch(name, name == currentName));
        }
        return GitResult<IReadOnlyList<GitBranch>>.Success(branches);
    }

    public async Task<GitResult> CreateBranchAsync(string worktreePath, string name, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "switch", "-c", name).ConfigureAwait(false));

    public async Task<GitResult> SwitchBranchAsync(string worktreePath, string name, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "switch", name).ConfigureAwait(false));

    // IGitStash — Save / Pop / List
    public async Task<GitResult> StashAsync(string worktreePath, string? message, CancellationToken ct = default)
        => ToResult(string.IsNullOrWhiteSpace(message)
            ? await RunAsync(worktreePath, ct, "stash", "push").ConfigureAwait(false)
            : await RunAsync(worktreePath, ct, "stash", "push", "-m", message).ConfigureAwait(false));

    public async Task<GitResult> StashPopAsync(string worktreePath, CancellationToken ct = default)
        => ToResult(await RunAsync(worktreePath, ct, "stash", "pop").ConfigureAwait(false));

    public async Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string worktreePath, CancellationToken ct = default)
    {
        var r = await RunAsync(worktreePath, ct, "stash", "list", "--format=%gd: %gs").ConfigureAwait(false);
        if (!r.Ok) return GitResult<IReadOnlyList<string>>.Fail(r.Stderr);
        var list = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        return GitResult<IReadOnlyList<string>>.Success(list);
    }

    private static GitResult ToResult(ProcOutcome p) => p.Ok ? GitResult.Success() : GitResult.Fail(p.Stderr);

    private readonly record struct ProcOutcome(bool Ok, string Stdout, string Stderr);

    private static async Task<ProcOutcome> RunAsync(string workingDir, CancellationToken ct, params string[] args)
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

            // Fork OFF the caller's thread. Process.Start = fork()/exec() runs SYNCHRONOUSLY at this
            // point, and callers reach RunAsync on the UI thread (RefreshGitPanelFor -> the git loaders).
            // A fork on the dispatcher contends with HarfBuzz text-shaping on the libmalloc fork-lock and
            // froze the cockpit under repo churn. Hop to the pool so no git subprocess ever forks on the
            // UI thread; the rest of this method already runs off-thread via ConfigureAwait(false).
            using var proc = await Task.Run(() =>
            {
                if (ForkThreadProbe.Value is { } box) box.Value = Environment.CurrentManagedThreadId;
                return Process.Start(psi);
            }, ct).ConfigureAwait(false);
            if (proc is null) return new ProcOutcome(false, "", "failed to start git");
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcOutcome(proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcOutcome(false, "", ex.Message);
        }
    }

    // Finder-launched .apps don't inherit the login PATH; resolve git explicitly (matches GitCliReader).
    private static string ResolveGit()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" })
            if (File.Exists(p)) return p;
        return "git";
    }
}
