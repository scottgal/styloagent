using Styloagent.Core.Git;

namespace Styloagent.Git;

public interface IGitDiff
{
    Task<GitResult<FileDiff>> GetDiffAsync(string worktreePath, string relativePath, bool staged, CancellationToken ct = default);
}
