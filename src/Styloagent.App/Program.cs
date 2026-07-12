using Avalonia;
using Mostlylucid.Avalonia.UITesting;

namespace Styloagent.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
