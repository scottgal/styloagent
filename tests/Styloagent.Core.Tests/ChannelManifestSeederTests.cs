using Styloagent.Core.Seeding;

public class ChannelManifestSeederTests
{
    [Fact]
    public async Task Seeds_one_entry_per_saved_context_file()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "channel");
        var map = new Dictionary<string, string> { ["foss-"] = "/repo/wt-foss" };
        var seeder = new ChannelManifestSeeder();

        var entries = await seeder.SeedAsync(root, map);

        var foss = Assert.Single(entries, e => e.Prefix == "foss-");
        Assert.Equal("/repo/wt-foss", foss.Worktree);
        Assert.EndsWith("foss-context.md", foss.SavedContextPath);
        Assert.EndsWith("foss-restart.md", foss.RestartPromptPath);
    }

    [Fact]
    public async Task Unmapped_prefix_still_seeds_with_empty_worktree()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "channel");
        var seeder = new ChannelManifestSeeder();

        var entries = await seeder.SeedAsync(root, new Dictionary<string, string>());

        Assert.Contains(entries, e => e.Prefix == "overview-" && e.Worktree == "");
    }

    [Fact]
    public async Task Missing_saved_context_directory_returns_empty_list()
    {
        var root = Path.Combine(Path.GetTempPath(), $"no-channel-{Guid.NewGuid():N}");
        var seeder = new ChannelManifestSeeder();

        var entries = await seeder.SeedAsync(root, new Dictionary<string, string>());

        Assert.Empty(entries);
    }

    // A writer that appended a separator to a prefix that already ended in one left channel roots
    // holding both `foss-context.md` and `foss--context.md`. Stripping the suffix blindly turns the
    // second file into a second agent literally named `foss--`, so the cockpit lists a phantom
    // beside the real one and mints a matching `foss--restart.md`. The doubled file must collapse
    // onto the agent it was written for, never spawn a new one.
    [Fact]
    public async Task A_doubled_separator_file_does_not_seed_a_phantom_agent()
    {
        var root = NewTempChannel();
        Write(root, "foss-context.md", "the real checkpoint");
        Write(root, "foss--context.md", "the stale doubled one");

        var entries = await new ChannelManifestSeeder().SeedAsync(root, new Dictionary<string, string>());

        var foss = Assert.Single(entries);
        Assert.Equal("foss-", foss.Prefix);
        Assert.EndsWith("foss-context.md", foss.SavedContextPath);
    }

    // Deduping must not drop the agent: with nothing but the doubled file on disk, that file is
    // still the best checkpoint we have, so it seeds under the corrected prefix and points at it.
    [Fact]
    public async Task A_lone_doubled_separator_file_still_seeds_its_agent()
    {
        var root = NewTempChannel();
        Write(root, "foss--context.md", "the only checkpoint");

        var entries = await new ChannelManifestSeeder().SeedAsync(root, new Dictionary<string, string>());

        var foss = Assert.Single(entries);
        Assert.Equal("foss-", foss.Prefix);
        Assert.EndsWith("foss--context.md", foss.SavedContextPath);
    }

    private static string NewTempChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"channel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "saved-context"));
        Directory.CreateDirectory(Path.Combine(root, "launch-prompts"));
        return root;
    }

    private static void Write(string root, string name, string body) =>
        File.WriteAllText(Path.Combine(root, "saved-context", name), body);
}
