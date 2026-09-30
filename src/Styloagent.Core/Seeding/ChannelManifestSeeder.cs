using Styloagent.Core.Model;

namespace Styloagent.Core.Seeding;

// CA1822: methods below are intentionally instance members (stateless service kept instantiable
// for the `new X().M()` call pattern used across the app/tests); do not make static.
#pragma warning disable CA1822

public sealed class ChannelManifestSeeder
{
    public Task<IReadOnlyList<AgentManifestEntry>> SeedAsync(
        string channelRoot, IReadOnlyDictionary<string, string> prefixToWorktree)
    {
        var savedContextDir = Path.Combine(channelRoot, "saved-context");
        var launchDir = Path.Combine(channelRoot, "launch-prompts");
        var entries = new List<AgentManifestEntry>();

        if (!Directory.Exists(savedContextDir))
            return Task.FromResult<IReadOnlyList<AgentManifestEntry>>(entries);

        // One file per agent. Reading a separator into the prefix is enough to invent an agent, so
        // files that differ only by a doubled separator (both `foss-context.md` and `foss--context.md`
        // are on disk after a writer appended one to a prefix that already ended in one) must collapse
        // onto a single entry, or the cockpit lists a phantom beside the real agent.
        foreach (var group in Directory.EnumerateFiles(savedContextDir, "*-context.md")
                     .GroupBy(PrefixOf, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var prefix = group.Key;
            var file = BestCheckpoint(group, prefix);
            var restart = Path.Combine(launchDir, $"{prefix}restart.md");
            entries.Add(new AgentManifestEntry(
                Prefix: prefix,
                Repo: "",
                Worktree: prefixToWorktree.TryGetValue(prefix, out var wt) ? wt : "",
                LaunchPromptPath: File.Exists(restart) ? restart : "",
                RestartPromptPath: File.Exists(restart) ? restart : "",
                SavedContextPath: file,
                Transport: AgentTransport.Local));
        }
        return Task.FromResult<IReadOnlyList<AgentManifestEntry>>(entries);
    }

    /// <summary>The agent a checkpoint belongs to: "foss-context.md" and "foss--context.md" are both "foss-".</summary>
    private static string PrefixOf(string file)
    {
        var name = Path.GetFileName(file);                  // "foss-context.md"
        var prefix = name[..^"context.md".Length];           // "foss-" or "foss--"
        return prefix.TrimEnd('-') + "-";
    }

    // Within one agent, the file whose name matches the prefix exactly is the one the agent itself
    // reads; a doubled-separator file is a stale leftover. Prefer the exact match, otherwise take the
    // most recent, so a lone doubled file still cold-starts its agent.
    private static string BestCheckpoint(IGrouping<string, string> files, string prefix)
    {
        var exact = files.FirstOrDefault(f => Path.GetFileName(f) == $"{prefix}context.md");
        return exact ?? files.OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.Ordinal)
            .First();
    }
}
