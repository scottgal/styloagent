using Styloagent.Core.Config;
using Styloagent.Core.Model;
using Xunit;

public class ManifestStoreTests
{
    [Fact]
    public async Task Save_then_Load_roundtrips_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.yaml");
        var entries = new List<AgentManifestEntry>
        {
            new("foss-", "/repo", "/repo/wt-foss", "/ch/launch-prompts/foss.md",
                "/ch/launch-prompts/foss-restart.md", "/ch/saved-context/foss-context.md",
                AgentTransport.Local),
        };
        var store = new ManifestStore();

        await store.SaveAsync(path, entries);
        var loaded = await store.LoadAsync(path);

        Assert.Single(loaded);
        Assert.Equal("foss-", loaded[0].Prefix);
        Assert.Equal(TransportKind.Local, loaded[0].Transport.Kind);
    }

    [Fact]
    public async Task Save_then_Load_roundtrips_ssh_transport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.yaml");
        var entries = new List<AgentManifestEntry>
        {
            new("deploy-", "/repo", "/repo/wt-deploy", "/ch/lp/deploy.md",
                "/ch/lp/deploy-restart.md", "/ch/sc/deploy-context.md",
                new AgentTransport(TransportKind.Ssh, "staging.example.net", "deploy-cred-ref")),
        };
        var store = new ManifestStore();

        await store.SaveAsync(path, entries);
        var loaded = await store.LoadAsync(path);

        var e = Assert.Single(loaded);
        Assert.Equal(TransportKind.Ssh, e.Transport.Kind);
        Assert.Equal("staging.example.net", e.Transport.SshHost);
        Assert.Equal("deploy-cred-ref", e.Transport.CredentialRef);
    }

    [Fact]
    public async Task Save_then_Load_roundtrips_codex_runtime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.yaml");
        var entries = new List<AgentManifestEntry>
        {
            new("codex-", "/repo", "/repo/wt-codex", "/ch/lp/codex.md",
                "/ch/lp/codex-restart.md", "/ch/sc/codex-context.md",
                AgentTransport.Local, AgentRuntimeKind.Codex),
        };
        var store = new ManifestStore();

        await store.SaveAsync(path, entries);
        var loaded = await store.LoadAsync(path);

        var e = Assert.Single(loaded);
        Assert.Equal(AgentRuntimeKind.Codex, e.Runtime);
    }
}
