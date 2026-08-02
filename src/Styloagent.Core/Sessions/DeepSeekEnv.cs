namespace Styloagent.Core.Sessions;

/// <summary>
/// Reads DeepSeek environment variables from a local env file so the Claude CLI can use
/// DeepSeek models as its backend. The file lives at <c>~/.styloagent/deepseek.env</c>
/// (one key for all projects) with an optional per-project override at
/// <c>.styloagent/deepseek.env</c>.
/// </summary>
public static class DeepSeekEnv
{
    /// <summary>The canonical path for the global DeepSeek env file.</summary>
    public static readonly string GlobalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".styloagent",
        "deepseek.env");

    /// <summary>
    /// Only env-var names matching these prefixes (case-insensitive) are forwarded to the child process.
    /// Everything else is dropped, including dangerous names like LD_PRELOAD, PATH, HOME, etc.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    {
        "ANTHROPIC_",
        "CLAUDE_CODE_",
    };

    /// <summary>
    /// Returns the env vars needed to route Claude Code through DeepSeek.
    /// Reads the global file and overlays a per-project override if it exists.
    /// Returns an empty dictionary when no env file is found (the runtime will use
    /// Anthropic defaults).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string? projectRoot)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);

        // Global: ~/.styloagent/deepseek.env
        LoadFile(GlobalPath, vars);

        // Per-project override: <repo>/.styloagent/deepseek.env
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectPath = Path.Combine(projectRoot, ".styloagent", "deepseek.env");
            LoadFile(projectPath, vars);
        }

        return vars;
    }

    private static void LoadFile(string path, Dictionary<string, string> vars)
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;

                var key = trimmed[..eq].Trim();
                var value = trimmed[(eq + 1)..].Trim();

                // Reject keys outside the allowlist — prevents injection of LD_PRELOAD,
                // PATH, HOME, PYTHONPATH, and other dangerous env vars.
                if (!AllowedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Strip surrounding quotes if present
                if (value.Length >= 2 &&
                    ((value.StartsWith('"') && value.EndsWith('"')) ||
                     (value.StartsWith('\'') && value.EndsWith('\''))))
                    value = value[1..^1];

                vars[key] = value;
            }
        }
        catch
        {
            // Malformed env file — degrade gracefully (no DeepSeek vars).
        }
    }
}
