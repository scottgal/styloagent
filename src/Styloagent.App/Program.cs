using Avalonia;
using Mostlylucid.Avalonia.UITesting;

namespace Styloagent.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // macOS .app bundle: Playwright resolves its driver next to the assembly (.playwright under
        // Contents/MacOS), but the release bundle ships it under Contents/Resources so codesign can
        // sign the bundle (codesign rejects the .playwright directory inside Contents/MacOS). Point
        // the driver at the bundled copy; outside a bundle (dev builds, Linux/Windows zips) the
        // standard location is used.
        ResolveBundledPlaywrightDriver();

        // Ownership PreToolUse gate-mode: a hook re-invokes us with the gate flag; decide on stdin→stdout
        // and exit BEFORE Avalonia starts (fast, headless, no window). See OwnershipGateCli. This runs per
        // edit independent of the running cockpit, so a frozen/closed cockpit can never stall or disable an
        // edit — the gate degrades to allow, never blocks the fleet by being unavailable.
        if (Styloagent.Core.Hooks.OwnershipGateCli.IsGateMode(args))
        {
            Styloagent.Core.Hooks.OwnershipGateCli.RunGateMode(args, System.Console.In, System.Console.Out);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Sets <c>PLAYWRIGHT_DRIVER_PATH</c> to the driver bundled under <c>Contents/Resources/.playwright</c>
    /// when running from a macOS .app bundle, so browser automation keeps working from the packaged app.
    /// No-op when the env var is already set, on non-macOS, or outside a bundle layout.
    /// </summary>
    private static void ResolveBundledPlaywrightDriver()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_PATH"))) return;
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var macosDir = Path.GetDirectoryName(exe);
        if (macosDir is null || !string.Equals(Path.GetFileName(macosDir), "MacOS", StringComparison.Ordinal)) return;
        var driver = Path.Combine(Path.GetDirectoryName(macosDir) ?? macosDir, "Resources", ".playwright", "node");
        if (File.Exists(driver))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_PATH", driver);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            // Enables real-platform UX driving + screenshots when launched with --mlui-test/--mlui-mcp/
            // --mlui-repl; a no-op for normal launches. Lets the UI test framework drive the actual
            // rendered app (real frame loop → dock/terminal content realizes).
            .UseUITesting();
}
