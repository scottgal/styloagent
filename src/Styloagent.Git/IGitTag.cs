using Styloagent.Core.Git;

namespace Styloagent.Git;

public sealed record GitTag(string Name, string TargetSha);

public interface IGitTag
{
    Task<GitResult<IReadOnlyList<GitTag>>> ListTagsAsync(string worktreePath, CancellationToken ct = default);
    Task<GitResult> CreateAnnotatedTagAsync(string worktreePath, string name, string message, CancellationToken ct = default);
    Task<GitResult> PushTagsAsync(string worktreePath, CancellationToken ct = default);
}
