using Styloagent.Core.Presentation;

namespace Styloagent.Core.Workspace;

/// <summary>One repo in a workspace: absolute path, display name, and a stable index (drives its hue).</summary>
public sealed record RepoRef(string Path, string Name, int Index);

/// <summary>
/// An overview agent to open for a repo: its addressable channel prefix, the repo root it runs in, that
/// repo's own system-prompt path (the specialist team travels with the repo), a stable index that drives
/// its hue, the resolved colour, and whether it is the workspace's primary (anchor) repo.
/// </summary>
public sealed record RepoOverview(
    string Prefix,
    string RepoRoot,
    string SystemPromptPath,
    int RepoIndex,
    string ColorHex,
    bool IsPrimary);

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

    /// <summary>
    /// The overview agents to open, one per repo. Single-repo keeps the historical <c>overview-</c> prefix and
    /// this workspace's overview system prompt (back-compat). Multi-repo names each overview after its repo
    /// (<c>styloagent-</c>) and points at that repo's own <c>.styloagent/system-prompt.md</c>, so the primary
    /// repo (index 0) anchors and every additional repo brings its own specialist overview onto the shared bus.
    /// Colliding repo names are disambiguated so every prefix stays unique.
    /// </summary>
    public IReadOnlyList<RepoOverview> RepoOverviews()
    {
        if (IsSingleRepo)
        {
            var r = Repos[0];
            return new[]
            {
                new RepoOverview("overview-", r.Path, OverviewSystemPromptPath, 0, RepoPalette.AgentColor(0, 0), IsPrimary: true),
            };
        }

        var used = new HashSet<string>();
        var list = new List<RepoOverview>(Repos.Count);
        foreach (var r in Repos)
        {
            var prefix = PrefixFor(r.Name);
            if (!used.Add(prefix))
            {
                prefix = PrefixFor($"{r.Name}-{r.Index}");   // two repos share a name → keep prefixes unique
                used.Add(prefix);
            }
            list.Add(new RepoOverview(
                Prefix: prefix,
                RepoRoot: r.Path,
                SystemPromptPath: Path.Combine(r.Path, ".styloagent", "system-prompt.md"),
                RepoIndex: r.Index,
                ColorHex: RepoPalette.AgentColor(r.Index, 0),
                IsPrimary: r.Index == 0));
        }
        return list;
    }

    /// <summary>Repo name → a clean, unique-ish channel prefix (lower-case alphanumerics, trailing <c>-</c>).</summary>
    internal static string PrefixFor(string repoName)
    {
        var cleaned = new string(repoName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        return (cleaned.Length == 0 ? "repo" : cleaned) + "-";
    }

    private static string NameOf(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));
}
