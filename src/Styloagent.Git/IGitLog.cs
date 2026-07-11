using Styloagent.Core.Git;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.Git;

/// <summary>
/// Narrow seam for reading commit history as vendored <see cref="Commit"/> objects.
/// Defined in <c>Styloagent.Git</c> (not Core) so it can return the vendored type
/// without forcing <c>Styloagent.Core</c> to take a dependency on the vendored models.
/// </summary>
public interface IGitLog
{
    Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default);
}
