using VYaml.Serialization;

namespace Styloagent.Core.Channel;

/// <summary>
/// Reads an optional <c>&lt;channelRoot&gt;/worktrees.yaml</c> that maps each agent prefix to the working
/// directory it should be revived in — so a reconstituted specialist launches in its real repo/worktree
/// (e.g. <c>foss-</c> → the stylobot checkout) instead of the app default. Absent or corrupt yields an empty
/// map (every agent falls back to the default working directory). Format is a plain YAML string→string map:
/// <code>
/// foss-: /Users/me/RiderProjects/stylobot
/// deploy-: /Users/me/RiderProjects/infra
/// </code>
/// </summary>
public static class WorktreeMapReader
{
    /// <summary>The prefix→worktree map for a channel, or empty when there is no (readable) worktrees.yaml.</summary>
    public static IReadOnlyDictionary<string, string> Read(string channelRoot)
    {
        var path = Path.Combine(channelRoot, "worktrees.yaml");
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            var map = YamlSerializer.Deserialize<Dictionary<string, string>>(
                new ReadOnlyMemory<byte>(File.ReadAllBytes(path)));
            return map ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }
    }
}
