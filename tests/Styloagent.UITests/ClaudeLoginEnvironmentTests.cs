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
    public void PtyEnvironment_OverridesTheMacOsDumbTerm_SoTuisRenderInColour()
    {
        // The bug: a .app launched from Finder/launchd has TERM=dumb in the PROCESS env, which claude/kilo
        // read as "no colour support" and render monochrome. BuildEnvironment must replace it.
        var oldTerm = Environment.GetEnvironmentVariable("TERM");
        var oldColor = Environment.GetEnvironmentVariable("COLORTERM");
        Environment.SetEnvironmentVariable("TERM", "dumb");
        Environment.SetEnvironmentVariable("COLORTERM", "");
        try
        {
            var env = PortaPtyLauncher.BuildEnvironment(null);   // no per-agent override of TERM
            Assert.Equal("xterm-256color", env["TERM"]);
            Assert.Equal("truecolor", env["COLORTERM"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERM", oldTerm);
            Environment.SetEnvironmentVariable("COLORTERM", oldColor);
        }
    }

    [Fact]
    public void PtyEnvironment_PreservesARealColourCapableTerm()
    {
        var env = PortaPtyLauncher.BuildEnvironment(
            new Dictionary<string, string> { ["TERM"] = "screen-256color" });

        Assert.Equal("screen-256color", env["TERM"]);
    }

    [Fact]
    public void PtyEnvironment_PreservesAnExplicitBrowserOverride()
    {
        var env = PortaPtyLauncher.BuildEnvironment(
            new Dictionary<string, string> { ["BROWSER"] = "/custom/browser" });

        Assert.Equal("/custom/browser", env["BROWSER"]);
    }
}
