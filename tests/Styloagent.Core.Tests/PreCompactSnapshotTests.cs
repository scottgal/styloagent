using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

/// <summary>
/// The PreCompact FALLBACK: a raw best-effort snapshot written right before a hard compaction, for when
/// the 0.80 checkpoint nudge was missed and the agent's resume doc would otherwise be empty. It reads the
/// agent's recent transcript (no agent turn, no PTY buffer needed) and writes it to the saved-context path
/// — but NEVER clobbers a doc that already has content (degrade-never-destroy), and never throws.
/// </summary>
public class PreCompactSnapshotTests : IDisposable
{
    private readonly string _dir;

    public PreCompactSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "precompact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Writes a minimal Claude transcript whose last assistant turn is <paramref name="text"/>.</summary>
    private string WriteTranscript(string text)
    {
        var path = Path.Combine(_dir, "session.jsonl");
        var line = $"{{\"type\":\"assistant\",\"message\":{{\"role\":\"assistant\",\"content\":" +
                   $"[{{\"type\":\"text\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}]}}}}";
        File.WriteAllText(path, line + "\n");
        return path;
    }

    [Fact]
    public void Captures_the_transcript_tail_when_the_resume_doc_is_missing()
    {
        var transcript = WriteTranscript("Mid widget refactor; next: wire the click handler.");
        var doc = Path.Combine(_dir, "session-context.md");   // does NOT exist yet

        var wrote = PreCompactSnapshot.Capture("session-", transcript, doc);

        Assert.True(wrote);
        Assert.True(File.Exists(doc));
        Assert.Contains("wire the click handler", File.ReadAllText(doc));
    }

    [Fact]
    public void Marks_the_snapshot_as_a_raw_pre_compaction_fallback()
    {
        var transcript = WriteTranscript("some recent work");
        var doc = Path.Combine(_dir, "bus-context.md");

        PreCompactSnapshot.Capture("bus-", transcript, doc);

        var body = File.ReadAllText(doc);
        Assert.Contains("bus-", body);
        Assert.Contains("pre-compaction fallback", body);   // clearly labelled so it's not mistaken for the good doc
    }

    [Fact]
    public void Does_not_clobber_an_existing_agent_authored_doc()
    {
        var transcript = WriteTranscript("raw transcript tail that must NOT overwrite the good doc");
        var doc = Path.Combine(_dir, "session-context.md");
        File.WriteAllText(doc, "# AGENT AUTHORED RESUME\nDistilled state the agent wrote at the 0.80 nudge.");

        var wrote = PreCompactSnapshot.Capture("session-", transcript, doc);

        Assert.False(wrote);
        var body = File.ReadAllText(doc);
        Assert.Contains("AGENT AUTHORED RESUME", body);          // preserved
        Assert.DoesNotContain("raw transcript tail", body);      // not overwritten
    }

    [Fact]
    public void Fills_an_empty_doc_placeholder()
    {
        var transcript = WriteTranscript("recovered from the transcript");
        var doc = Path.Combine(_dir, "session-context.md");
        File.WriteAllText(doc, "   \n");   // present but effectively empty → still a would-be-empty reload

        var wrote = PreCompactSnapshot.Capture("session-", transcript, doc);

        Assert.True(wrote);
        Assert.Contains("recovered from the transcript", File.ReadAllText(doc));
    }

    [Fact]
    public void Best_effort_returns_false_without_a_transcript_and_never_throws()
    {
        var doc = Path.Combine(_dir, "session-context.md");

        Assert.False(PreCompactSnapshot.Capture("session-", null, doc));
        Assert.False(PreCompactSnapshot.Capture("session-", Path.Combine(_dir, "does-not-exist.jsonl"), doc));
        Assert.False(File.Exists(doc));   // nothing written when there's nothing to snapshot
    }

    [Fact]
    public void Returns_false_when_there_is_nowhere_to_write()
    {
        var transcript = WriteTranscript("anything");
        Assert.False(PreCompactSnapshot.Capture("session-", transcript, null));
        Assert.False(PreCompactSnapshot.Capture("session-", transcript, "   "));
    }
}
