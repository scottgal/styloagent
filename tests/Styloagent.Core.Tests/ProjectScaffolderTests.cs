using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class ProjectScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Ensure_creates_config_tree_and_default_files()
    {
        Directory.CreateDirectory(_root);
        var cfg = ProjectScaffolder.Ensure(_root);

        Assert.True(File.Exists(cfg.SystemPromptPath));
        Assert.True(File.Exists(cfg.ProtocolPath));
        Assert.True(Directory.Exists(Path.Combine(cfg.ChannelRoot, "inbox")));
        Assert.True(Directory.Exists(Path.Combine(cfg.ChannelRoot, "archive", "outbox")));
        Assert.True(Directory.Exists(cfg.LaunchPromptsDir));
        Assert.True(Directory.Exists(Path.Combine(cfg.EnvironmentsRoot, "definitions")));
        Assert.True(Directory.Exists(Path.Combine(cfg.BrowserRoot, "jobs")));
        Assert.Equal("controlOwner: overview-\n", File.ReadAllText(Path.Combine(cfg.EnvironmentsRoot, "policy.yaml")));
        Assert.Equal(Path.Combine(_root, ".styloagent"), cfg.ConfigDir);
    }

    [Fact]
    public void Ensure_is_idempotent_and_never_overwrites_edited_files()
    {
        Directory.CreateDirectory(_root);
        var cfg = ProjectScaffolder.Ensure(_root);
        File.WriteAllText(cfg.SystemPromptPath, "MY EDITED PROMPT");

        var cfg2 = ProjectScaffolder.Ensure(_root); // second run

        Assert.Equal("MY EDITED PROMPT", File.ReadAllText(cfg2.SystemPromptPath));
    }
}
