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

    /// <summary>
    /// Builds a transcript far larger than the tail window, with the newest usage on the LAST line.
    /// </summary>
    private static string WriteBigTranscript(string newestLine, int padKb = 1024)
    {
        var path = Path.GetTempFileName();
        var filler = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + new string('x', 900) + "\"}}";
        using var w = new StreamWriter(path);
        for (int i = 0; i < padKb; i++) w.Write(filler + "\n");
        w.Write(newestLine + "\n");
        return path;
    }

    [Fact]
    public void ReadLatest_finds_usage_on_the_last_line_of_a_large_transcript()
    {
        var path = WriteBigTranscript(SampleLines[2]);
        try
        {
            var usage = TranscriptReader.ReadLatest(path);
            Assert.NotNull(usage);
            Assert.Equal(83000, usage!.ContextTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tail_scan_does_not_corrupt_multi_byte_utf8()
    {
        // The tail is split on the '\n' BYTE; UTF-8 is self-synchronising so that is safe, but a naive
        // byte-slice decode would still mangle a multi-byte glyph that straddles a slice boundary.
        var line = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":"
                 + "[{\"type\":\"text\",\"text\":\"héllo — 世界 🎉 done\"}]}}";
        var path = WriteBigTranscript(line);
        try
        {
            Assert.Equal("héllo — 世界 🎉 done", TranscriptReader.ReadLastAssistantText(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The cockpit refreshes every agent's usage readout off this call every ~3s, so its cost is paid
    /// per-pane forever. Reading the tail by decoding the WHOLE 256 KB window to a UTF-16 string and then
    /// String.Split-ing it allocated ~1.3 MB per call — across a live fleet that was ~11% of ALL process
    /// allocation, feeding the background-GC churn that made the cockpit sluggish. The usage line is
    /// normally within a few lines of the end, so decode lazily, one line at a time, from the end.
    /// </summary>
    [Fact]
    public void ReadLatest_does_not_allocate_the_whole_tail_window()
    {
        var path = WriteBigTranscript(SampleLines[2]);
        try
        {
            TranscriptReader.ReadLatest(path);   // warm up JIT + the array pool

            var before = GC.GetAllocatedBytesForCurrentThread();
            var usage = TranscriptReader.ReadLatest(path);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.NotNull(usage);
            Assert.True(allocated < 16 * 1024,
                $"ReadLatest allocated {allocated:N0} bytes scanning the tail; expected well under the "
                + "256 KB tail window (it should decode only the handful of lines it actually reads).");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Context_over_200k_establishes_the_1m_window_even_without_a_1m_model_id()
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
    public void Explicit_configured_1m_signal_establishes_window_below_200k()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":50000}}}");
            var usage = TranscriptReader.ReadLatest(path, configuredModel: "claude-opus-4-8[1m]");
            Assert.NotNull(usage);
            Assert.Equal(1_000_000, usage!.WindowTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ambiguous_early_1m_capable_model_is_unavailable_without_a_reliable_signal()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":50000}}}");
            Assert.Null(TranscriptReader.ReadLatest(path));
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

    /// <summary>
    /// The claude-deepseek runtime writes deepseek model ids into the same Claude transcript, and DeepSeek
    /// models carry no "1m" marker — yet their native window is ~1M. A deepseek agent measured against the
    /// 200k default would show ~5x inflated fill and false pressure advisories.
    /// </summary>
    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek/deepseek-v4-flash")]
    [InlineData("DeepSeek-V4-Pro")]
    public void Deepseek_model_uses_1m_window(string model)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":50000}}}");
            var usage = TranscriptReader.ReadLatest(path);
            Assert.NotNull(usage);
            Assert.Equal(1_000_000, usage!.WindowTokens);
            Assert.Equal(0.05, usage.ContextFraction, 3);
            Assert.Equal(950_000, usage.RemainingTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Configured_deepseek_model_establishes_1m_window()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":50000}}}");
            var usage = TranscriptReader.ReadLatest(path, configuredModel: "deepseek-v4-pro");
            Assert.NotNull(usage);
            Assert.Equal(1_000_000, usage!.WindowTokens);
        }
        finally { File.Delete(path); }
    }
}
