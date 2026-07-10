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

        foreach (var file in Directory.EnumerateFiles(savedContextDir, "*-context.md").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);                 // "foss-context.md"
            var prefix = name[..^"context.md".Length];          // "foss-"
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
}
