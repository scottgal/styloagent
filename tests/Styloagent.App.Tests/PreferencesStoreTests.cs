using Styloagent.App.Config;
using Xunit;

namespace Styloagent.App.Tests;

public class PreferencesStoreTests
{
    [Fact]
    public async Task Round_trips_all_preferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "styloagent-prefs-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            var store = new PreferencesStore();
            await store.SaveAsync(path, new AppPreferences
            {
                LightTheme = true, Accent = "Teal", TerminalTheme = "Dracula",
                TerminalFontSize = 15, MarkdownFontSize = 16,
            });

            var loaded = await store.LoadAsync(path);

            Assert.True(loaded.LightTheme);
            Assert.Equal("Teal", loaded.Accent);
            Assert.Equal("Dracula", loaded.TerminalTheme);
            Assert.Equal(15, loaded.TerminalFontSize);
            Assert.Equal(16, loaded.MarkdownFontSize);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Missing_file_yields_defaults_and_is_not_purple()
    {
        var prefs = await new PreferencesStore().LoadAsync("/no/such/dir/styloagent-prefs.yaml");
        Assert.Equal("Blue", prefs.Accent);   // default accent is NOT the old purple
        Assert.False(prefs.LightTheme);
    }

    [Fact]
    public void Accent_resolve_is_case_insensitive_and_falls_back_to_default()
    {
        Assert.Equal("Teal", AccentPalette.Resolve("teal").Name);
        Assert.Equal("Blue", AccentPalette.Resolve("nonsense").Name);
        Assert.Equal("Blue", AccentPalette.Resolve(null).Name);
    }
}
