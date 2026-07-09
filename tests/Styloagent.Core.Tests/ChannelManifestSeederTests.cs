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
}
