using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

[YamlObject]
internal partial class ProposedAgentsFile
{
    public List<ProposedAgentRow> Agents { get; set; } = new();
}

[YamlObject]
internal partial class ProposedAgentRow
{
    public string Prefix { get; set; } = "";
    public string Responsibility { get; set; } = "";
    public string Dir { get; set; } = ".";
    public string LaunchPrompt { get; set; } = "";
    public bool Worktree { get; set; }
}

/// <summary>Reads <c>proposed-agents.yaml</c> into <see cref="ProposedAgent"/>s. Never throws.</summary>
public static class ProposedAgentsReader
{
    public static IReadOnlyList<ProposedAgent> Read(string path)
    {
        if (!File.Exists(path)) return Array.Empty<ProposedAgent>();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var file = YamlSerializer.Deserialize<ProposedAgentsFile>(new ReadOnlyMemory<byte>(bytes));
            var list = new List<ProposedAgent>();
            foreach (var r in file.Agents)
            {
                if (string.IsNullOrWhiteSpace(r.Prefix)) continue;
                list.Add(new ProposedAgent(r.Prefix.Trim(), r.Responsibility.Trim(),
                    string.IsNullOrWhiteSpace(r.Dir) ? "." : r.Dir.Trim(), r.LaunchPrompt, r.Worktree));
            }
            return list;
        }
        catch { return Array.Empty<ProposedAgent>(); }
    }
}
