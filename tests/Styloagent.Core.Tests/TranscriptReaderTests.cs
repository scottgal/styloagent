using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Tests;

public class TranscriptReaderTests
{
    private static readonly string[] AssistantLines =
    {
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"first turn\"}]}}",
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}",
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"},{\"type\":\"text\",\"text\":\"the final answer\"}]}}",
    };

    [Fact]
    public void ReadLastAssistantText_returns_the_latest_assistant_text_skipping_tool_only()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join('\n', AssistantLines));
            Assert.Equal("the final answer", TranscriptReader.ReadLastAssistantText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadLastAssistantText_missing_file_is_null()
        => Assert.Null(TranscriptReader.ReadLastAssistantText("/no/such/t.jsonl"));

    private static readonly string[] SampleLines =
    {
        "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4\",\"usage\":{\"input_tokens\":1000,\"cache_read_input_tokens\":40000,\"output_tokens\":500}}}",
        "{\"type\":\"user\",\"message\":{\"role\":\"user\"}}",
        "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8-1m\",\"usage\":{\"input_tokens\":2000,\"cache_read_input_tokens\":80000,\"cache_creation_input_tokens\":1000,\"output_tokens\":600}}}",
    };

    [Fact]
    public void EscapeCwd_replaces_every_non_alphanumeric_with_dash()
    {
        Assert.Equal("-Users-scott-RiderProjects-mostlylucid-atoms",
            TranscriptReader.EscapeCwd("/Users/scott/RiderProjects/mostlylucid.atoms"));
    }

    [Fact]
    public void PathFor_is_null_without_cwd_or_session()
    {
        Assert.Null(TranscriptReader.PathFor(null, "abc"));
        Assert.Null(TranscriptReader.PathFor("/x", null));
        Assert.NotNull(TranscriptReader.PathFor("/x", "abc"));
    }

    [Fact]
    public void ReadLatest_reads_the_last_assistant_usage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join('\n', SampleLines));

            var usage = TranscriptReader.ReadLatest(path);

            Assert.NotNull(usage);
            Assert.Equal(83000, usage!.ContextTokens);       // 2000 + 80000 + 1000 (last message)
            Assert.Equal(1_000_000, usage.WindowTokens);     // model id contains "1m"
            Assert.Equal(0.083, usage.ContextFraction, 3);
            Assert.Equal(917_000, usage.RemainingTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadLatest_missing_file_is_null()
        => Assert.Null(TranscriptReader.ReadLatest("/no/such/transcript.jsonl"));

    [Fact]
    public void Context_over_200k_infers_the_1m_window_even_without_a_1m_model_id()
    {
        // Real transcripts read model "claude-opus-4-8" even on a 1M session, so size must decide.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"cache_read_input_tokens\":584000}}}");
            var usage = TranscriptReader.ReadLatest(path);
            Assert.Equal(1_000_000, usage!.WindowTokens);
            Assert.Equal(0.584, usage.ContextFraction, 3);
            Assert.Equal(416_000, usage.RemainingTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Non_1m_model_uses_200k_window()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4\",\"usage\":{\"input_tokens\":50000}}}");
            var usage = TranscriptReader.ReadLatest(path);
            Assert.Equal(200_000, usage!.WindowTokens);
            Assert.Equal(0.25, usage.ContextFraction, 3);
        }
        finally { File.Delete(path); }
    }
}
