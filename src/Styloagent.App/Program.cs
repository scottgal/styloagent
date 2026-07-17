using Avalonia;
using Mostlylucid.Avalonia.UITesting;

namespace Styloagent.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
