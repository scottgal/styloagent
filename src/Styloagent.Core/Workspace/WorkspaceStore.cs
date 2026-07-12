using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Workspace;

[YamlObject]
internal partial class WorkspaceFile
{
    public string Name { get; set; } = "";
    public List<string> Repos { get; set; } = new();
}

// CA1822: instance methods by design (mirrors the other *Store `new X().M()` usage).
#pragma warning disable CA1822

/// <summary>
/// Loads/saves a workspace's <c>.styloagent-workspace/workspace.yaml</c> (repo list). Repo paths may be
/// absolute, <c>~</c>-relative, or relative to the workspace root; they are resolved on load. Tolerant:
/// a missing/corrupt file yields null (caller falls back to single-repo). Sync <see cref="Load"/> for
/// the startup path — never block an async read on the UI thread.
/// </summary>
public sealed class WorkspaceStore
{
    /// <summary>The workspace.yaml path under a workspace root.</summary>
    public static string PathFor(string workspaceRoot) => Path.Combine(workspaceRoot, ".styloagent-workspace", "workspace.yaml");

    /// <summary>Loads the workspace at <paramref name="workspaceRoot"/>, or null if it has no workspace.yaml.</summary>
    public WorkspaceConfig? Load(string workspaceRoot)
    {
        var path = PathFor(workspaceRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var file = YamlSerializer.Deserialize<WorkspaceFile>(new ReadOnlyMemory<byte>(File.ReadAllBytes(path)));
            if (file is null) return null;
            var repos = file.Repos
                .Select(r => Resolve(r, workspaceRoot))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            return WorkspaceConfig.For(workspaceRoot, file.Name, repos);
        }
        catch { return null; }
    }

    public async Task SaveAsync(string workspaceRoot, string name, IReadOnlyList<string> repoPaths)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(workspaceRoot, ".styloagent-workspace"));
            var file = new WorkspaceFile { Name = name, Repos = repoPaths.ToList() };
            var bytes = YamlSerializer.Serialize(file);
            await File.WriteAllBytesAsync(PathFor(workspaceRoot), bytes.ToArray()).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private static string Resolve(string repo, string workspaceRoot)
    {
        var r = repo.Trim();
        if (r.StartsWith('~'))
            r = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), r[1..].TrimStart('/', '\\'));
        return Path.IsPathRooted(r) ? Path.GetFullPath(r) : Path.GetFullPath(Path.Combine(workspaceRoot, r));
    }
}
