using Styloagent.App.Config;
using Xunit;

namespace Styloagent.App.Tests;

public class RecentProjectsStoreTests
{
    [Fact]
    public async Task Add_puts_most_recent_first_dedupes_and_caps()
    {
        var file = Path.Combine(Path.GetTempPath(), "recents-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            var store = new RecentProjectsStore();
            for (int i = 0; i < 10; i++)
                await store.AddAsync(file, "/proj/" + i);
            await store.AddAsync(file, "/proj/3");   // re-add an existing one

            var recents = await store.LoadAsync(file);

            Assert.Equal("/proj/3", recents[0]);        // most-recent first
            Assert.True(recents.Count <= 8);            // capped
            Assert.Single(recents, r => r == "/proj/3"); // de-duplicated
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task Load_returns_empty_when_missing()
        => Assert.Empty(await new RecentProjectsStore().LoadAsync("/no/such/recents.yaml"));
}
