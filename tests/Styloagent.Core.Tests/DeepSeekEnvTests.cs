using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class DeepSeekEnvTests
{
    [Fact]
    public void Load_does_not_forward_a_pinned_Claude_Code_effort_level()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-deepseek-env-{Guid.NewGuid():N}");
        var configDirectory = Path.Combine(root, ".styloagent");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "deepseek.env");

        try
        {
            File.WriteAllText(configPath, "ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic\n" +
                                          "CLAUDE_CODE_EFFORT_LEVEL=max\n");

            var env = DeepSeekEnv.Load(root);

            Assert.Equal("https://api.deepseek.com/anthropic", env["ANTHROPIC_BASE_URL"]);
            Assert.DoesNotContain(env.Keys,
                key => key.Equals("CLAUDE_CODE_EFFORT_LEVEL", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
