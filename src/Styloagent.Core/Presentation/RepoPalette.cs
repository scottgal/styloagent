namespace Styloagent.Core.Presentation;

/// <summary>
/// Assigns each repo in a workspace a distinct base hue, and gives each agent in a repo a colour from
/// that hue family (same hue, varied saturation/lightness), so an agent's repo is glanceable across the
/// roster, tabs and timeline. Pure and deterministic; the colour system for multi-repo workspaces.
/// </summary>
public static class RepoPalette
{
    // Well-separated base hues (degrees), cycled across repos: blue, green, amber, purple, rose,
    // teal, lime, orange. Chosen to stay distinct at a glance even side by side.
    private static readonly double[] BaseHues = { 212, 145, 38, 275, 340, 188, 96, 18 };

    /// <summary>The number of distinct base hues before they cycle.</summary>
    public static int HueCount => BaseHues.Length;

    /// <summary>Base hue (degrees, 0–360) for a repo by its index; cycles for more repos than hues.</summary>
    public static double BaseHueFor(int repoIndex)
        => BaseHues[((repoIndex % BaseHues.Length) + BaseHues.Length) % BaseHues.Length];

    /// <summary>
    /// A <c>#RRGGBB</c> colour for an agent: its repo's hue, with saturation/lightness varied per agent
    /// so agents in one repo share a family yet stay distinguishable.
    /// </summary>
    public static string AgentColor(int repoIndex, int agentIndex)
    {
        var hue = BaseHueFor(repoIndex);
        var i = agentIndex < 0 ? 0 : agentIndex;
        var sat = 0.58 + 0.12 * (i % 3);            // 0.58 / 0.70 / 0.82
        var light = 0.64 - 0.09 * ((i / 3) % 3);    // 0.64 / 0.55 / 0.46
        return HslToHex(hue, sat, light);
    }

    /// <summary>The repo's base colour itself (for section headers / badges) — mid saturation/lightness.</summary>
    public static string RepoColor(int repoIndex) => HslToHex(BaseHueFor(repoIndex), 0.62, 0.55);

    private static string HslToHex(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;

        (double r, double g, double b) = h switch
        {
            < 60  => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _     => (c, 0.0, x),
        };

        int R = Clamp255((r + m) * 255), G = Clamp255((g + m) * 255), B = Clamp255((b + m) * 255);
        return $"#{R:X2}{G:X2}{B:X2}";
    }

    private static int Clamp255(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
}
