using Styloagent.Terminal;

namespace Styloagent.UITests;

public sealed class ClaudeLoginEnvironmentTests
{
    [Fact]
    public void PtyEnvironment_ProvidesTheHostBrowserLauncher_ForClaudeLogin()
    {
        var env = PortaPtyLauncher.BuildEnvironment(null);
        var expected = PortaPtyLauncher.PreferredBrowserLauncher();

        if (expected is not null)
            Assert.Equal(expected, env["BROWSER"]);
    }

    [Fact]
    public void PtyEnvironment_PreservesAnExplicitBrowserOverride()
    {
        var env = PortaPtyLauncher.BuildEnvironment(
            new Dictionary<string, string> { ["BROWSER"] = "/custom/browser" });

        Assert.Equal("/custom/browser", env["BROWSER"]);
    }
}
