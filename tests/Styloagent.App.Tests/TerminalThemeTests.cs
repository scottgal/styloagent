using Styloagent.Terminal;
using Xunit;

namespace Styloagent.App.Tests;

public class TerminalThemeTests
{
    [Fact]
    public void All_includes_the_built_in_presets()
    {
        var names = TerminalTheme.All.Select(t => t.Name).ToList();
        Assert.Contains("Default", names);
        Assert.Contains("Solarized", names);
        Assert.Contains("Matrix", names);
        Assert.Contains("Dracula", names);
        Assert.Contains("Light", names);
    }

    [Fact]
    public void Default_matches_the_terminal_default_colours()
    {
        Assert.Equal(0xFF0C0C0Cu, TerminalTheme.Default.Background);
        Assert.Equal(0xFFEDEDEDu, TerminalTheme.Default.Foreground);
    }

    [Fact]
    public void Themes_are_distinct()
    {
        var bgs = TerminalTheme.All.Select(t => t.Background).ToHashSet();
        Assert.Equal(TerminalTheme.All.Count, bgs.Count);   // each theme has a distinct background
    }
}
