using Styloagent.Core.Model;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Config;

// CA1822: methods below are intentionally instance members (stateless service kept instantiable
// for the `new X().M()` call pattern used across the app/tests); do not make static.
#pragma warning disable CA1822

[YamlObject]
internal partial class ManifestFile
{
    public List<ManifestRow> Agents { get; set; } = new();
}

[YamlObject]
internal partial class ManifestRow
{
    public string Prefix { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Worktree { get; set; } = "";
    public string LaunchPrompt { get; set; } = "";
    public string RestartPrompt { get; set; } = "";
    public string SavedContext { get; set; } = "";
    public string Transport { get; set; } = "local";
    public string Runtime { get; set; } = "claude";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? SshHost { get; set; }
    public string? CredentialRef { get; set; }
}

public sealed class ManifestStore
{
    public async Task SaveAsync(string path, IReadOnlyList<AgentManifestEntry> entries)
    {
        var file = new ManifestFile
        {
            Agents = entries.Select(e => new ManifestRow
            {
                Prefix = e.Prefix,
                Repo = e.Repo,
                Worktree = e.Worktree,
                LaunchPrompt = e.LaunchPromptPath,
                RestartPrompt = e.RestartPromptPath,
                SavedContext = e.SavedContextPath,
                Transport = e.Transport.Kind == TransportKind.Ssh ? "ssh" : "local",
                Runtime = e.Runtime == AgentRuntimeKind.Codex ? "codex" : "claude",
                Model = e.Model,
                Effort = e.Effort,
                SshHost = e.Transport.SshHost,
                CredentialRef = e.Transport.CredentialRef,
            }).ToList(),
        };
        var bytes = YamlSerializer.Serialize(file);
        await File.WriteAllBytesAsync(path, bytes.ToArray());
    }

    public async Task<IReadOnlyList<AgentManifestEntry>> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var file = YamlSerializer.Deserialize<ManifestFile>(new ReadOnlyMemory<byte>(bytes));
        return file.Agents.Select(r => new AgentManifestEntry(
            r.Prefix,
            r.Repo,
            r.Worktree,
            r.LaunchPrompt,
            r.RestartPrompt,
            r.SavedContext,
            r.Transport == "ssh"
                ? new AgentTransport(TransportKind.Ssh, r.SshHost, r.CredentialRef)
                : AgentTransport.Local,
            string.Equals(r.Runtime, "codex", StringComparison.OrdinalIgnoreCase)
                ? AgentRuntimeKind.Codex
                : AgentRuntimeKind.Claude,
            r.Model,
            r.Effort)).ToList();
    }
}
