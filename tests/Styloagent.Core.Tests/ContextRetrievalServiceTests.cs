using Styloagent.Core.Channel;
using Styloagent.Core.Issues;
using Styloagent.Core.Memory;
using Styloagent.Core.Retrieval;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class ContextRetrievalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "styloagent-context-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Retrieve_combines_citeable_docs_active_bus_and_open_issues_but_omits_archived_bus()
    {
        var cfg = Path.Combine(_root, ".styloagent");
        var channel = Path.Combine(cfg, "channel");
        var issues = Path.Combine(cfg, "issues");
        var memory = Path.Combine(cfg, "memory");
        Directory.CreateDirectory(memory);
        await File.WriteAllTextAsync(Path.Combine(_root, "design.md"), "# Browser approval\n\nPlaywright runs need owner approval.");
        ChannelMessageWriter.Write(channel, "overview-", "worker-", "browser approval", "Approve the Playwright run.", "urgent", DateTimeOffset.UtcNow);
        ChannelMessageWriter.Write(channel, "overview-", "worker-", "old resolved", "This is historical.", "normal", DateTimeOffset.UtcNow.AddMinutes(-2));
        ChannelMessageWriter.Reply(channel, "worker-", "old resolved", "done", DateTimeOffset.UtcNow.AddMinutes(-1));
        IssueStore.Write(issues, "overview-", "Browser approval blocks staging", "Need approval for Playwright browser access.", "high", DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(memory, "rule.md"), "---\nname: ⭐ Browser safety\ndescription: Browser approval rule\npin: true\n---\nRequire owner approval.");
        var options = new MemoryRagOptions(memory, Path.Combine(cfg, "index.json"), "", "");

        var result = await ContextRetrievalService.RetrieveAsync(_root, channel, issues, ["worker-"], options, "worker-",
            "browser approval playwright", ["docs", "bus", "issues", "memory"], 12, 12_000);

        Assert.True(result.Candidates["docs"] > 0);
        Assert.Equal(1, result.Candidates["bus"]);
        Assert.Equal(1, result.Candidates["issues"]);
        Assert.Contains(result.Hits, h => h.Source == "docs" && h.Title.Contains("Browser approval"));
        Assert.Contains(result.Hits, h => h.Source == "bus" && h.State == "attention");
        Assert.Contains(result.Hits, h => h.Source == "issues" && h.State == "high");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
