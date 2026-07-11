using Styloagent.Core.Issues;
using Styloagent.Core.Projects;

namespace Styloagent.Core.Git;

/// <summary>What to wrap up: the agent, its repo, its worktree, and its branch.</summary>
public sealed record WrapUpRequest(string Prefix, string RepoRoot, string WorktreePath, string Branch);

/// <summary>How a wrap-up ended.</summary>
public enum WrapUpStatus { Merged, KeptUncommitted, KeptTestsFailed, KeptConflict }

/// <summary>Outcome of a wrap-up; on a kept failure carries the filed issue id.</summary>
public sealed record WrapUpOutcome(WrapUpStatus Status, string Message, string? IssueId)
{
    public bool Merged => Status == WrapUpStatus.Merged;
}

/// <summary>
/// Gated auto-merge: guard clean → run tests → merge → clean up. Any failure keeps the worktree and
/// (for test/conflict failures) files a high-severity issue. Never merges dirty/failing/conflicting work.
/// </summary>
public sealed class WrapUpService
{
    private readonly IGitService _git;
    private readonly ITestRunner _tests;

    public WrapUpService(IGitService git, ITestRunner tests) => (_git, _tests) = (git, tests);

    public async Task<WrapUpOutcome> WrapUpAsync(WrapUpRequest req, GitPolicy policy, string issuesDir, CancellationToken ct = default)
    {
        // 1. Guard clean.
        var status = await _git.GetStatusAsync(req.WorktreePath, ct).ConfigureAwait(false);
        if (status.Ok && status.Value!.IsDirty)
            return new WrapUpOutcome(WrapUpStatus.KeptUncommitted,
                $"{req.Prefix} has uncommitted changes — commit or discard before wrap-up.", null);

        // 2. Run tests (if configured).
        if (!string.IsNullOrWhiteSpace(policy.TestCommand))
        {
            var test = await _tests.RunAsync(req.WorktreePath, policy.TestCommand!, ct).ConfigureAwait(false);
            if (!test.Passed)
            {
                var id = FileIssue(issuesDir, req, $"wrap-up blocked: tests failed on {req.Branch}", test.Output);
                return new WrapUpOutcome(WrapUpStatus.KeptTestsFailed,
                    $"tests failed for {req.Prefix}; worktree kept, issue {id} filed.", id);
            }
        }

        // 3. Merge.
        var merge = await _git.MergeNoFfAsync(req.RepoRoot, req.Branch, policy.MainBranch, ct).ConfigureAwait(false);
        if (!merge.Ok)
        {
            await _git.AbortMergeAsync(req.RepoRoot, ct).ConfigureAwait(false);
            var id = FileIssue(issuesDir, req, $"wrap-up blocked: merge conflict on {req.Branch}", merge.Error ?? "merge conflict");
            return new WrapUpOutcome(WrapUpStatus.KeptConflict,
                $"merge conflict for {req.Prefix}; worktree kept, issue {id} filed.", id);
        }

        // 4. Clean up.
        if (policy.RemoveWorktreeOnMerge)
        {
            await _git.RemoveWorktreeAsync(req.RepoRoot, req.WorktreePath, ct).ConfigureAwait(false);
            await _git.DeleteBranchAsync(req.RepoRoot, req.Branch, force: false, ct).ConfigureAwait(false);
        }
        return new WrapUpOutcome(WrapUpStatus.Merged,
            $"{req.Prefix} merged into {policy.MainBranch} and cleaned up.", null);
    }

    private static string FileIssue(string issuesDir, WrapUpRequest req, string title, string detail)
        => IssueStore.Write(issuesDir, $"wt-{req.Prefix.TrimEnd('-')}", title, detail, "high", DateTimeOffset.Now).Id;
}
