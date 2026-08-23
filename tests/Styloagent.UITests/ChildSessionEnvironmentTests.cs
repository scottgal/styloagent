using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// A spawned agent must start as its OWN top-level Claude Code session, never as a continuation of whatever
/// session launched the cockpit.
///
/// The launcher copies the whole parent environment to every PTY child. When the cockpit is itself started
/// from inside a Claude Code session (e.g. `./run.sh` run from an agent), that environment carries Claude
/// Code's per-session markers — and every agent inherits them. <c>CLAUDE_CODE_CHILD_SESSION</c> makes Claude
/// Code treat the process as a nested child and TURN TRANSCRIPT SAVING OFF, which the operator sees as
/// "Transcript saving is off — inherited CLAUDE_CODE_CHILD_SESSION marker" in every pane. That is not
/// cosmetic: Styloagent reads those transcripts for each agent's token/context readout
/// (<c>TranscriptReader.ReadLatest</c>), so the usage display goes dead too. <c>CLAUDE_CODE_SESSION_ID</c>
/// leaks the launching session's identity into every agent for the same reason.
/// </summary>
public class ChildSessionEnvironmentTests
{
    [Theory]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("CLAUDE_CODE_ENTRYPOINT")]
    public void An_agent_never_inherits_the_launching_sessions_markers(string marker)
    {
        var original = Environment.GetEnvironmentVariable(marker);
        try
        {
            Environment.SetEnvironmentVariable(marker, "inherited-from-the-cockpits-own-session");

            var env = PortaPtyLauncher.BuildEnvironment(null);

            Assert.False(env.ContainsKey(marker),
                $"spawned agents inherited {marker} from the process that launched the cockpit");
        }
        finally { Environment.SetEnvironmentVariable(marker, original); }
    }

    /// <summary>
    /// Scrubbing is about the LAUNCHING session's leakage, not a muzzle: an explicit per-agent override
    /// must still win, so the spawn pipeline can set these deliberately when it means to.
    /// </summary>
    [Fact]
    public void An_explicit_override_still_wins()
    {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "inherited");

            var env = PortaPtyLauncher.BuildEnvironment(
                new Dictionary<string, string> { ["CLAUDE_CODE_CHILD_SESSION"] = "deliberate" });

            Assert.Equal("deliberate", env["CLAUDE_CODE_CHILD_SESSION"]);
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", original); }
    }

    [Fact]
    public void An_agent_cannot_inherit_or_override_a_pinned_effort_level()
    {
        const string setting = "CLAUDE_CODE_EFFORT_LEVEL";
        var original = Environment.GetEnvironmentVariable(setting);
        try
        {
            Environment.SetEnvironmentVariable(setting, "max");

            var env = PortaPtyLauncher.BuildEnvironment(
                new Dictionary<string, string> { [setting] = "high" });

            Assert.False(env.ContainsKey(setting));
        }
        finally { Environment.SetEnvironmentVariable(setting, original); }
    }

    /// <summary>Unrelated inherited environment (PATH, auth, user config) must be left completely alone.</summary>
    [Fact]
    public void Everything_else_is_still_inherited()
    {
        var original = Environment.GetEnvironmentVariable("STYLOAGENT_TEST_KEEPME");
        try
        {
            Environment.SetEnvironmentVariable("STYLOAGENT_TEST_KEEPME", "keep");

            var env = PortaPtyLauncher.BuildEnvironment(null);

            Assert.Equal("keep", env["STYLOAGENT_TEST_KEEPME"]);
            Assert.True(env.ContainsKey("PATH"));
        }
        finally { Environment.SetEnvironmentVariable("STYLOAGENT_TEST_KEEPME", original); }
    }
}
