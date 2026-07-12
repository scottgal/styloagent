namespace Styloagent.Core.Workspace;

/// <summary>One repo in a workspace: absolute path, display name, and a stable index (drives its hue).</summary>
public sealed record RepoRef(string Path, string Name, int Index);

/// <summary>
/// A workspace of N repos sharing one bus and coordinated by a workspace overview. Resolved paths only;
/// pure (no I/O). A single repo opened directly is modelled as a workspace of one (<see cref="SingleRepo"/>).
/// </summary>
public sealed record WorkspaceConfig(
    string Name,
    string WorkspaceRoot,
    string ConfigDir,
    string ChannelRoot,
    string OverviewSystemPromptPath,
    IReadOnlyList<RepoRef> Repos,
    bool IsSingleRepo)
{
    /// <summary>A multi-repo workspace rooted at <paramref name="workspaceRoot"/> over <paramref name="repoPaths"/>.</summary>
    public static WorkspaceConfig For(string workspaceRoot, string? name, IReadOnlyList<string> repoPaths)
    {
        var cfg = Path.Combine(workspaceRoot, ".styloagent-workspace");
        var repos = repoPaths
            .Select((p, i) => new RepoRef(p, NameOf(p), i))
            .ToList();
        return new WorkspaceConfig(
            Name: string.IsNullOrWhiteSpace(name) ? NameOf(workspaceRoot) : name!,
            WorkspaceRoot: workspaceRoot,
            ConfigDir: cfg,
            ChannelRoot: Path.Combine(cfg, "channel"),
            OverviewSystemPromptPath: Path.Combine(cfg, "workspace-overview.md"),
            Repos: repos,
            IsSingleRepo: repos.Count <= 1);
    }

    /// <summary>Back-compat: a workspace of a single repo — its own channel, no separate workspace overview.</summary>
    public static WorkspaceConfig SingleRepo(string repoRoot)
    {
        var cfg = Path.Combine(repoRoot, ".styloagent");
        return new WorkspaceConfig(
            Name: NameOf(repoRoot),
            WorkspaceRoot: repoRoot,
            ConfigDir: cfg,
            ChannelRoot: Path.Combine(cfg, "channel"),
            OverviewSystemPromptPath: Path.Combine(cfg, "system-prompt.md"),
            Repos: new[] { new RepoRef(repoRoot, NameOf(repoRoot), 0) },
            IsSingleRepo: true);
    }

    private static string NameOf(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));
}
