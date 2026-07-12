using VYaml.Annotations;

namespace Styloagent.App.Config;

/// <summary>
/// User-tweakable, persisted global preferences (theme, accent, font sizes). Saved to
/// <c>ApplicationData/Styloagent/preferences.yaml</c> and applied at startup. Distinct from
/// per-project presentation (agent colours) and the recent-projects list.
/// </summary>
[YamlObject]
public partial class AppPreferences
{
    /// <summary>Light vs dark structural theme (Fluent variant).</summary>
    public bool LightTheme { get; set; }

    /// <summary>Named accent preset (see <see cref="AccentPalette"/>). Default is Blue — not the old purple.</summary>
    public string Accent { get; set; } = AccentPalette.DefaultName;

    /// <summary>Global terminal colour theme name (see <c>TerminalTheme.All</c>).</summary>
    public string TerminalTheme { get; set; } = "Default";

    /// <summary>Terminal font size in points. Clamped to a sane range when applied.</summary>
    public double TerminalFontSize { get; set; } = 13;

    /// <summary>Markdown / document font size in points.</summary>
    public double MarkdownFontSize { get; set; } = 14;

    /// <summary>
    /// Whether agents may drive/observe the cockpit via the UI-automation MCP tool (screenshots).
    /// OFF by default — a privileged introspection surface. Enabling it broadcasts a bus notice.
    /// </summary>
    public bool EnableUiAutomation { get; set; }
}

/// <summary>One named accent: the bright accent (buttons/highlights) and its darker cockpit-bar shade.</summary>
public sealed record AccentPreset(string Name, string Accent, string Cockpit);

/// <summary>
/// The accent choices exposed in Settings. The default is <c>Blue</c> — the previous hardcoded purple
/// is kept only as an explicit "Purple" option, no longer the default.
/// </summary>
public static class AccentPalette
{
    public const string DefaultName = "Blue";

    public static readonly IReadOnlyList<AccentPreset> All = new[]
    {
        new AccentPreset("Blue",   "#3B82F6", "#1E4F9E"),
        new AccentPreset("Teal",   "#14B8A6", "#0C6E64"),
        new AccentPreset("Green",  "#3FB950", "#256B30"),
        new AccentPreset("Amber",  "#E5A05A", "#A6692E"),
        new AccentPreset("Rose",   "#F43F5E", "#9F1239"),
        new AccentPreset("Slate",  "#6B7D97", "#3E4A63"),
        new AccentPreset("Purple", "#9D7FE0", "#7C5CBF"),
    };

    /// <summary>Resolves a preset by name (case-insensitive), falling back to the default.</summary>
    public static AccentPreset Resolve(string? name)
        => All.FirstOrDefault(a => string.Equals(a.Name, name, System.StringComparison.OrdinalIgnoreCase))
           ?? All.First(a => a.Name == DefaultName);
}
