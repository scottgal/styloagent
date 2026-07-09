using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Styloagent.UITests;

/// <summary>
/// xUnit collection fixture that boots a single Avalonia headless session for the
/// whole test run. Tests opt in via [Collection("Avalonia")] and use
/// DispatchAsync to marshal work onto the headless UI thread.
/// </summary>
public sealed class HeadlessAvaloniaFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessAvaloniaFixture()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
    }

    public Task<T> DispatchAsync<T>(Func<T> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<Task<T>> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Action work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Func<Task> work) =>
        _session.Dispatch<bool>(async () => { await work(); return true; }, CancellationToken.None);

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Minimal Avalonia application used to bootstrap the headless platform.
/// <para>
/// BuildAvaloniaApp is discovered by HeadlessUnitTestSession.StartNew and used to
/// configure the AppBuilder.
/// </para>
/// </summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Pre-load FluentIcons assemblies so their avares:// resources are accessible.
        _ = typeof(FluentIcons.Avalonia.Fluent.SymbolIcon).Assembly;
        try
        {
            System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(
                new System.Reflection.AssemblyName("FluentIcons.Resources.Avalonia"));
        }
        catch { /* already loaded */ }

        return AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .ConfigureFonts(fm =>
            {
                fm.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(
                    new Uri("fonts:Seagull Fluent Icons"),
                    new Uri("avares://FluentIcons.Resources.Avalonia/Assets")));
            });
    }

    public override void Initialize()
    {
        // Load the SAME themes the real App.axaml uses, so headless tests render with real control
        // templates. Without DockFluentTheme, DockControl has no dock-model→control templates and
        // renders nothing — which previously got misdiagnosed as "Dock.Avalonia is broken".
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Styloagent.App/App.axaml"))
        {
            Source = new Uri("avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml"),
        });

        // Suppress rendering exceptions caused by the CFF-format Seagull Fluent Icons
        // font failing to load in the headless Skia software renderer (a known limitation).
        // Tests check logical/binding structure, not visual font rendering.
        Dispatcher.UIThread.UnhandledExceptionFilter += (_, args) =>
        {
            if (args.Exception is InvalidOperationException ex
                && ex.Message.Contains("glyphTypeface", StringComparison.OrdinalIgnoreCase))
            {
                args.RequestCatch = true;
            }
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            if (args.Exception is InvalidOperationException ex
                && ex.Message.Contains("glyphTypeface", StringComparison.OrdinalIgnoreCase))
            {
                args.Handled = true;
            }
        };
    }
}

/// <summary>Marker so all Avalonia tests share one headless session.</summary>
[CollectionDefinition("Avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvaloniaFixture> { }
