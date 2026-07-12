using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Styloagent.App.Config;

/// <summary>
/// Applies user preferences to live Avalonia resources: the accent preset (which repaints every
/// <c>{DynamicResource AccentBrush}</c> / <c>CockpitAccentBrush</c> consumer and Fluent's
/// <c>SystemAccentColor</c>) and the light/dark theme variant.
/// </summary>
public static class ThemeApplier
{
    /// <summary>Repoints the accent brushes + Fluent SystemAccentColor from a preset.</summary>
    public static void ApplyAccent(Application app, AccentPreset preset)
    {
        if (Color.TryParse(preset.Accent, out var accent))
        {
            app.Resources["AccentBrush"] = new SolidColorBrush(accent);
            app.Resources["SystemAccentColor"] = accent;   // drives Fluent controls (toggles, etc.)
        }
        if (Color.TryParse(preset.Cockpit, out var cockpit))
            app.Resources["CockpitAccentBrush"] = new SolidColorBrush(cockpit);
    }

    /// <summary>Sets the app-wide light/dark Fluent theme variant.</summary>
    public static void ApplyThemeVariant(Application app, bool light)
        => app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
}
