using System.Globalization;
using Styloagent.Core.Presentation;

namespace Styloagent.Core.Tests;

public class RepoPaletteTests
{
    private static (int R, int G, int B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (int.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    // Hue (0-360) of an RGB colour — to assert two colours share a repo's hue family.
    private static double Hue(string hex)
    {
        var (r, g, b) = Rgb(hex);
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn)), min = Math.Min(rn, Math.Min(gn, bn)), d = max - min;
        if (d == 0) return 0;
        double h = max == rn ? ((gn - bn) / d) % 6 : max == gn ? (bn - rn) / d + 2 : (rn - gn) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    [Fact]
    public void Every_agent_color_is_a_valid_hex()
    {
        for (int repo = 0; repo < 10; repo++)
            for (int agent = 0; agent < 10; agent++)
            {
                var c = RepoPalette.AgentColor(repo, agent);
                Assert.Matches("^#[0-9A-F]{6}$", c);
            }
    }

    [Fact]
    public void Agents_in_one_repo_share_the_repo_hue_family()
    {
        var a = RepoPalette.AgentColor(1, 0);
        var b = RepoPalette.AgentColor(1, 4);   // different agent, same repo
        Assert.InRange(Math.Abs(Hue(a) - Hue(b)), 0, 6);   // same hue (within rounding)
    }

    [Fact]
    public void Different_repos_get_different_hues()
    {
        var repo0 = Hue(RepoPalette.AgentColor(0, 0));
        var repo1 = Hue(RepoPalette.AgentColor(1, 0));
        Assert.True(Math.Abs(repo0 - repo1) > 20, $"repo hues too close: {repo0} vs {repo1}");
    }

    [Fact]
    public void Agents_in_a_repo_are_distinguishable_from_each_other()
        => Assert.NotEqual(RepoPalette.AgentColor(0, 0), RepoPalette.AgentColor(0, 1));

    [Fact]
    public void Base_hue_cycles_and_is_deterministic()
    {
        Assert.Equal(RepoPalette.BaseHueFor(0), RepoPalette.BaseHueFor(RepoPalette.HueCount));
        Assert.Equal(RepoPalette.AgentColor(2, 3), RepoPalette.AgentColor(2, 3));
    }
}
