using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

/// <summary>
/// checkpoint-in-place — the PreCompact FALLBACK for the compaction-resilience feature. When a compaction
/// fires before an agent self-authored a fresh resume doc (the 0.80 nudge was missed), this writes a
/// best-effort anchor to its saved-context doc so the post-compaction reload isn't EMPTY. Distinct from
/// dehydrate: it never frees the terminal (file-only), and degrade-never-destroy: it NEVER overwrites an
/// existing agent-authored doc (even a stale one is richer than a mechanical anchor).
/// </summary>
public class InPlaceCheckpointTests
{
    private static string TempDoc(out string dir, bool create = false, string? content = null)
    {
        dir = Path.Combine(Path.GetTempPath(), "styloagent-ckpt-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "saved-context", "bus-context.md");
        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content ?? "");
        }
        return path;
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 17, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Writes_a_fallback_anchor_when_the_doc_is_missing()
    {
        var path = TempDoc(out var dir);
        try
        {
            var r = InPlaceCheckpoint.Write("bus-", path, fallbackBody: "You own Core/Channel.", Now);

            Assert.Equal(InPlaceCheckpointOutcome.Wrote, r.Outcome);
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("bus-", text);
            Assert.Contains("You own Core/Channel.", text);   // caller's fallback body carried
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Writes_when_the_existing_doc_is_blank()
    {
        var path = TempDoc(out var dir, create: true, content: "   \n");
        try
        {
            var r = InPlaceCheckpoint.Write("bus-", path, "anchor", Now);
            Assert.Equal(InPlaceCheckpointOutcome.Wrote, r.Outcome);
            Assert.Contains("anchor", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Never_overwrites_an_existing_agent_authored_doc()
    {
        var path = TempDoc(out var dir, create: true, content: "# my careful resume doc\nlots of state");
        try
        {
            var r = InPlaceCheckpoint.Write("bus-", path, "mechanical anchor", Now);

            Assert.Equal(InPlaceCheckpointOutcome.KeptExisting, r.Outcome);
            var text = File.ReadAllText(path);
            Assert.Contains("my careful resume doc", text);    // preserved
            Assert.DoesNotContain("mechanical anchor", text);  // fallback NOT written
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Blank_saved_context_path_degrades_to_a_failure_not_a_throw()
    {
        var r = InPlaceCheckpoint.Write("bus-", "   ", "anchor", Now);
        Assert.Equal(InPlaceCheckpointOutcome.Failed, r.Outcome);
    }
}
