using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.App.Config;

[YamlObject]
internal partial class RecentProjectsFile
{
    public List<string> Projects { get; set; } = new();
}

// CA1822: instance methods by design (mirrors PresentationStore's `new X().M()` usage).
#pragma warning disable CA1822

/// <summary>Persists a capped, de-duplicated, most-recent-first list of project roots (VYaml).</summary>
public sealed class RecentProjectsStore
{
    private const int Cap = 8;

    public async Task<IReadOnlyList<string>> LoadAsync(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var file = YamlSerializer.Deserialize<RecentProjectsFile>(new ReadOnlyMemory<byte>(bytes));
            return file.Projects;
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task AddAsync(string path, string projectRoot)
    {
        var current = (await LoadAsync(path)).ToList();
        current.RemoveAll(p => p == projectRoot);
        current.Insert(0, projectRoot);
        if (current.Count > Cap) current = current.GetRange(0, Cap);

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = YamlSerializer.Serialize(new RecentProjectsFile { Projects = current });
        await File.WriteAllBytesAsync(path, bytes.ToArray());
    }
}
