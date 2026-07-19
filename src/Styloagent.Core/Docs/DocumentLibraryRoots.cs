using Styloagent.Core.Workspace;

namespace Styloagent.Core.Docs;

/// <summary>One repository's independently browsable document root in a workspace.</summary>
public sealed record RepoDocumentRoot(string DisplayName, string RepoRoot, int RepoIndex);

/// <summary>
/// The document-library roots for one workspace. Repository roots deliberately remain separate: consumers
/// must render/browse each root independently rather than flattening documents from unrelated repositories.
/// Channel and agent logs are workspace-wide shared roots.
/// </summary>
public sealed record DocumentLibraryRoots(
    IReadOnlyList<RepoDocumentRoot> Repositories,
    string ChannelRoot,
    string? LogsRoot)
{
    /// <summary>
    /// Projects the workspace model into the roots a document-library presentation needs. A one-repository
    /// workspace retains the released <c>repo</c> section label; multi-repo labels use each repository name
    /// and deterministically disambiguate duplicate directory names.
    /// </summary>
    public static DocumentLibraryRoots For(WorkspaceConfig workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var nameCounts = workspace.Repos
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<RepoDocumentRoot>(workspace.Repos.Count);

        foreach (var repo in workspace.Repos)
        {
            var displayName = "repo";
            if (!workspace.IsSingleRepo)
            {
                occurrences.TryGetValue(repo.Name, out var occurrence);
                occurrence++;
                occurrences[repo.Name] = occurrence;
                displayName = nameCounts[repo.Name] == 1 ? repo.Name : $"{repo.Name} ({occurrence})";
            }

            repositories.Add(new RepoDocumentRoot(displayName, repo.Path, repo.Index));
        }

        return new DocumentLibraryRoots(
            repositories,
            workspace.ChannelRoot,
            DocLibraryReader.ResolveLogsRoot(workspace.ChannelRoot));
    }
}
