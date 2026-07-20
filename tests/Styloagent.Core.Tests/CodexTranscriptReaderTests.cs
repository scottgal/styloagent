using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Tests;

public class CodexTranscriptReaderTests
{
    [Fact]
    public void ReadLatest_reads_newest_token_count_with_runtime_window()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2048},"model_context_window":128000}}}
                {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":61440,"cached_input_tokens":60000},"model_context_window":258400}}}
                """);

            var usage = CodexTranscriptReader.ReadLatest(path);

            Assert.NotNull(usage);
            Assert.Equal(61_440, usage!.ContextTokens);
            Assert.Equal(258_400, usage.WindowTokens);
            Assert.Equal(0.238, usage.ContextFraction, 3);
            Assert.Equal(196_960, usage.RemainingTokens);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100}}}}")]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{},\"model_context_window\":128000}}}")]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"other\"}}")]
    public void ReadLatest_does_not_invent_missing_usage_or_window(string line)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, line);
            Assert.Null(CodexTranscriptReader.ReadLatest(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadLatest_missing_file_is_null()
        => Assert.Null(CodexTranscriptReader.ReadLatest("/no/such/codex-session.jsonl"));

    [Fact]
    public void PathForSession_blank_id_is_null()
        => Assert.Null(CodexTranscriptReader.PathForSession(" "));
}
