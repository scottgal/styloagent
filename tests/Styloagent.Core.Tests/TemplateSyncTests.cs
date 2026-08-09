using Styloagent.Core.Projects;
using Xunit;

public class TemplateSyncTests
{
    private static readonly string[] DocNames = { "system-prompt.md", "PROTOCOL.md", "model-policy.yaml" };

    private static readonly Styloagent.Core.Projects.TemplateSync.BundledTemplate[] V1Templates =
    {
        new("system-prompt.md", "# v1 system prompt\nold guidance.\n", IsMarkdown: true),
        new("PROTOCOL.md", "# v1 protocol\nold protocol.\n", IsMarkdown: true),
        new("model-policy.yaml", "default:\n  runtime: claude\n", IsMarkdown: false),
    };

    private static readonly Styloagent.Core.Projects.TemplateSync.BundledTemplate[] V2Templates =
    {
        new("system-prompt.md", "# v2 system prompt\nnew guidance — mixed fleets.\n", IsMarkdown: true),
        new("PROTOCOL.md", "# v2 protocol\nnew protocol.\n", IsMarkdown: true),
        new("model-policy.yaml", "default:\n  runtime: kilo\n  model: kilo/deepseek/deepseek-v4-pro\n", IsMarkdown: false),
    };

    private static string Root() => Path.Combine(Path.GetTempPath(), "tmpl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Fresh_project_gets_all_templates_and_state()
    {
        var root = Root();
        try
        {
            var cfg = ProjectConfig.For(root);
            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);

            foreach (var name in DocNames)
                Assert.True(File.Exists(Path.Combine(cfg.ConfigDir, name)), $"{name} should exist");

            // The state file records the version + per-file hashes so later syncs can diff.
            var statePath = TemplateSync.StatePathFor(cfg);
            Assert.True(File.Exists(statePath));
            var state = YamlDeserialize(statePath);
            Assert.Equal(1, state.Version);
            Assert.Equal(3, state.Files.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sync_is_idempotent_at_the_same_version()
    {
        var root = Root();
        try
        {
            var cfg = ProjectConfig.For(root);
            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);
            var promptPath = Path.Combine(cfg.ConfigDir, "system-prompt.md");
            var before = File.ReadAllText(promptPath);

            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);   // same version → no-op

            Assert.Equal(before, File.ReadAllText(promptPath));
            Assert.Equal("# v1 system prompt\nold guidance.\n", File.ReadAllText(promptPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Version_bump_updates_unedited_files_in_place()
    {
        var root = Root();
        try
        {
            var cfg = ProjectConfig.For(root);
            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);

            TemplateSync.Ensure(cfg, V2Templates, bundledVersion: 2);

            Assert.Equal("# v2 system prompt\nnew guidance — mixed fleets.\n",
                File.ReadAllText(Path.Combine(cfg.ConfigDir, "system-prompt.md")));
            Assert.Equal("# v2 protocol\nnew protocol.\n",
                File.ReadAllText(Path.Combine(cfg.ConfigDir, "PROTOCOL.md")));
            Assert.Contains("kilo/deepseek/deepseek-v4-pro",
                File.ReadAllText(Path.Combine(cfg.ConfigDir, "model-policy.yaml")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Agent_edited_markdown_gets_an_appended_update_not_a_clobber()
    {
        var root = Root();
        try
        {
            var cfg = ProjectConfig.For(root);
            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);

            // The agent customised its system prompt between versions.
            File.WriteAllText(Path.Combine(cfg.ConfigDir, "system-prompt.md"),
                "# v1 system prompt\nold guidance.\n\n## MY CUSTOM RULES\nnever use tests.\n");

            TemplateSync.Ensure(cfg, V2Templates, bundledVersion: 2);

            var updated = File.ReadAllText(Path.Combine(cfg.ConfigDir, "system-prompt.md"));
            Assert.Contains("## MY CUSTOM RULES", updated);                          // agent text preserved
            Assert.Contains("Styloagent template update (v1 → v2)", updated);        // update block appended
            Assert.Contains("mixed fleets", updated);                                // new content delivered
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Agent_edited_yaml_gets_a_notice_not_appended_prose()
    {
        var root = Root();
        try
        {
            var cfg = ProjectConfig.For(root);
            TemplateSync.Ensure(cfg, V1Templates, bundledVersion: 1);

            // The agent rewrote the policy; appending prose would corrupt the YAML.
            File.WriteAllText(Path.Combine(cfg.ConfigDir, "model-policy.yaml"),
                "default:\n  runtime: codex\n  reasoning: local choice\n");

            TemplateSync.Ensure(cfg, V2Templates, bundledVersion: 2);

            var policy = File.ReadAllText(Path.Combine(cfg.ConfigDir, "model-policy.yaml"));
            Assert.Equal("default:\n  runtime: codex\n  reasoning: local choice\n", policy);   // untouched
            var notice = Path.Combine(TemplateSync.UpdateNoticesDir(cfg), "model-policy.yaml.md");
            Assert.True(File.Exists(notice), "an update notice should be written");
            Assert.Contains("kilo/deepseek/deepseek-v4-pro", File.ReadAllText(notice));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Pre_versioning_project_with_old_templates_gets_an_update_not_a_clobber()
    {
        var root = Root();
        try
        {
            // A fleet seeded BEFORE versioning existed: files present, no template-state.yaml.
            var cfg = ProjectConfig.For(root);
            Directory.CreateDirectory(cfg.ConfigDir);
            File.WriteAllText(Path.Combine(cfg.ConfigDir, "system-prompt.md"),
                "# old unversioned system prompt\n");

            TemplateSync.Ensure(cfg, V2Templates, bundledVersion: 2);

            var updated = File.ReadAllText(Path.Combine(cfg.ConfigDir, "system-prompt.md"));
            Assert.Contains("# old unversioned system prompt", updated);
            Assert.Contains("Styloagent template update (v0 → v2)", updated);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Scaffolder_ensures_system_prompt_and_runs_the_sync()
    {
        var root = Root();
        try
        {
            var cfg = ProjectScaffolder.Ensure(root);

            // The explicit ask: a system prompt ALWAYS exists after scaffold, even for existing fleets.
            Assert.True(File.Exists(cfg.SystemPromptPath), "system-prompt.md must exist after scaffold");
            Assert.True(File.Exists(cfg.ProtocolPath));
            Assert.True(File.Exists(cfg.ModelPolicyPath));
            Assert.Contains("overview / architect", File.ReadAllText(cfg.SystemPromptPath));
            Assert.True(File.Exists(TemplateSync.StatePathFor(cfg)), "sync state should be recorded");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static StateShape YamlDeserialize(string path)
        => VYaml.Serialization.YamlSerializer.Deserialize<StateShape>(File.ReadAllBytes(path))!;
}


/// <summary>Public mirror of the internal TemplateState, just for test assertions on the state file.</summary>
[VYaml.Annotations.YamlObject]
public sealed partial class StateShape
{
    public int Version { get; set; }
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}
