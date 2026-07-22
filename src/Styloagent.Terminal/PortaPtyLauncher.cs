using Porta.Pty;
using Styloagent.Core.Sessions;

namespace Styloagent.Terminal;

/// <summary>
/// Implements <see cref="IPtyLauncher"/> using Porta.Pty's <see cref="PtyProvider"/>.
/// </summary>
public sealed class PortaPtyLauncher : IPtyLauncher
{
    public async Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
    {
        var ptyOptions = new PtyOptions
        {
            App = options.Command,
            CommandLine = options.Args.ToArray(),
            // Defense in depth: Porta.Pty throws ArgumentNullException on an empty Cwd,
            // so never pass one through — resolve to a real, existing directory.
            Cwd = WorkingDirectoryResolver.Resolve(options.WorkingDirectory),
            Cols = options.Cols,
            Rows = options.Rows,
            // Always pass an environment with a robust PATH so `claude` (and other user-installed
            // CLIs) resolve even when the app is launched from a macOS .app bundle, which inherits
            // only launchd's minimal PATH (/usr/bin:/bin:...) — no Homebrew or ~/.local/bin.
            Environment = BuildEnvironment(options.Env),
        };

        var connection = await PtyProvider.SpawnAsync(ptyOptions, ct).ConfigureAwait(false);
        return new PortaPtySession(connection);
    }

    /// <summary>
    /// Builds the child environment from the current process env, prepends the usual user-tool
    /// directories to PATH (so a bundle-launched app can still find <c>claude</c>), then overlays
    /// any explicit overrides from <paramref name="overrides"/>.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[(string)kv.Key] = kv.Value as string ?? string.Empty;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var toolDirs = new[]
        {
            Path.Combine(home, ".local", "bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
        };
        env.TryGetValue("PATH", out var existing);
        var prefix = string.Join(':', toolDirs);
        env["PATH"] = string.IsNullOrEmpty(existing) ? prefix : $"{prefix}:{existing}";

        // Claude Code's /login (and `claude auth login`) delegates OAuth to $BROWSER. A process launched
        // from a macOS .app commonly has no BROWSER even though it has a GUI session, so Claude falls back
        // to printing the URL inside the embedded PTY and the sign-in flow appears stuck. Point it at the
        // host OS launcher; Claude keeps ownership of the callback/code exchange and writes credentials to
        // the normal user keychain. Never replace an inherited or explicit user choice.
        EnsureBrowserLauncher(env);

        if (overrides is { Count: > 0 })
            foreach (var kv in overrides)
                env[kv.Key] = kv.Value;

        return env;
    }

    internal static string? PreferredBrowserLauncher()
    {
        if (OperatingSystem.IsMacOS()) return "/usr/bin/open";
        if (OperatingSystem.IsWindows()) return "explorer.exe";
        if (File.Exists("/usr/bin/xdg-open")) return "/usr/bin/xdg-open";
        if (File.Exists("/usr/bin/gio")) return "/usr/bin/gio open";
        return null;
    }

    internal static void EnsureBrowserLauncher(IDictionary<string, string> env)
    {
        if ((!env.TryGetValue("BROWSER", out var browser) || string.IsNullOrWhiteSpace(browser))
            && PreferredBrowserLauncher() is { } launcher)
            env["BROWSER"] = launcher;
    }
}
