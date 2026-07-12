using VYaml.Serialization;

namespace Styloagent.App.Config;

// CA1822: instance methods by design (mirrors RecentProjectsStore's `new X().M()` usage).
#pragma warning disable CA1822

/// <summary>
/// Loads/saves <see cref="AppPreferences"/> as VYaml (same pattern as <see cref="RecentProjectsStore"/>).
/// Tolerant: a missing or corrupt file yields fresh defaults, never throws into startup.
/// </summary>
public sealed class PreferencesStore
{
    /// <summary>The default path: <c>ApplicationData/Styloagent/preferences.yaml</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Styloagent", "preferences.yaml");

    public async Task<AppPreferences> LoadAsync(string path)
    {
        if (!File.Exists(path)) return new AppPreferences();
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            return YamlSerializer.Deserialize<AppPreferences>(new ReadOnlyMemory<byte>(bytes))
                   ?? new AppPreferences();
        }
        catch { return new AppPreferences(); }
    }

    public async Task SaveAsync(string path, AppPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var bytes = YamlSerializer.Serialize(prefs);
            await File.WriteAllBytesAsync(path, bytes.ToArray());
        }
        catch { /* preferences are best-effort; a failed save must never crash the app */ }
    }
}
