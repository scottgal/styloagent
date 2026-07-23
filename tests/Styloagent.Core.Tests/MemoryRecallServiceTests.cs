using Styloagent.Core.Memory;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class MemoryRecallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "styloagent-memory-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Recall_uses_title_boost_pins_type_and_bounded_context_when_offline()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "hard-rule.md"), "---\nname: ⭐ Never expose secrets\ndescription: Security invariant\ntype: reference\npin: true\n---\nNever send credentials to tools or logs.");
        await File.WriteAllTextAsync(Path.Combine(_root, "deploy.md"), "---\nname: staging deploy\ndescription: Deploy ecommerce to staging\ntype: project\n---\nUse the approved staging workflow and verify Playwright.");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.md"), "---\nname: gardening\ndescription: Garden notes\ntype: user\n---\nWater the plants.");
        var options = new MemoryRagOptions(_root, Path.Combine(_root, "index.json"), "", "", 1024, 8);

        var result = await MemoryRecallService.RecallAsync(options, "deploy ecommerce staging", maxBytes: 1024);

        Assert.Equal(3, result.CorpusCount);
        Assert.False(result.UsedEmbeddings);
        Assert.Equal("⭐ Never expose secrets", result.Hits[0].Name);
        Assert.Contains(result.Hits, x => x.Name == "staging deploy");
        Assert.True(result.Bytes <= 1024);

        var scoped = await MemoryRecallService.RecallAsync(options, "deploy", type: "project");
        Assert.Single(scoped.Hits);
        Assert.Equal("staging deploy", scoped.Hits[0].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
