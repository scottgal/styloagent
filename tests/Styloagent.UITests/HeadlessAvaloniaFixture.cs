using Avalonia;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;

[assembly: Xunit.TestCollectionOrderer("Styloagent.UITests.AvaloniaCollectionOrderer", "Styloagent.UITests")]
// Both collections share ONE headless session (single UI thread) — they must run serially, not in
// parallel, or their Dispatch calls interleave and wedge. (The suite was already serial when it was
// a single collection.)
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Styloagent.UITests;

/// <summary>
/// xUnit collection fixture that boots a single Avalonia headless session for the
/// whole test run. Tests opt in via [Collection("Avalonia")] and use
/// DispatchAsync to marshal work onto the headless UI thread.
/// </summary>
public sealed class HeadlessAvaloniaFixture : IDisposable
{
    // ONE session per process (Avalonia can only be initialized once): shared across every
    // [Collection] that uses this fixture, so a second collection can exist for isolating the
    // LiveMarkdown-render tests (which poison later tests in the shared session) to run last.
    private static readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.StartNew(typeof(TestApp));

    // Instance methods (the collection-fixture contract) that delegate to the shared static session.
#pragma warning disable CA1822
    public Task<T> DispatchAsync<T>(Func<T> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<Task<T>> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Action work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Func<Task> work) =>
        _session.Dispatch<bool>(async () => { await work(); return true; }, CancellationToken.None);
#pragma warning restore CA1822

    // Shared, process-lifetime session — do not dispose per collection.
    public void Dispose() { }
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

        // The shared theme tokens (CockpitBgBrush etc.) the real App.axaml merges — so headless
        // renders resolve them (and so light/dark is faithful in screenshots).
        Resources.MergedDictionaries.Add(
            new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Styloagent.App/App.axaml"))
            {
                Source = new Uri("avares://Styloagent.App/Themes/ThemeTokens.axaml"),
            });

        // Application-level DataTemplates, exactly as App.axaml declares them, so dock-hosted
        // content resolves its view the same way it does in the real app.
        DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
        DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
        // DiagramDocumentViewModel is more derived — register it before MarkdownDocumentViewModel
        // so Avalonia's first-match resolution picks the dedicated diagram view.
        DataTemplates.Add(new FuncDataTemplate<DiagramDocumentViewModel>((_, _) => new DiagramDocumentView(), true));
        DataTemplates.Add(new FuncDataTemplate<MarkdownDocumentViewModel>((_, _) => new MarkdownDocumentView(), true));

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

/// <summary>
/// Second collection sharing the SAME headless session, for tests that render LiveMarkdown. Those
/// leave the shared single-thread dispatcher in a state that wedges a later test (a missed-wakeup on
/// a Background-priority op); <see cref="AvaloniaCollectionOrderer"/> runs this collection LAST so
/// nothing runs after them and the suite completes. They pass fine themselves.
/// </summary>
[CollectionDefinition("Avalonia-Markdown")]
public sealed class AvaloniaMarkdownCollection : ICollectionFixture<HeadlessAvaloniaFixture> { }

/// <summary>Runs the "Avalonia-Markdown" collection after "Avalonia".</summary>
public sealed class AvaloniaCollectionOrderer : Xunit.ITestCollectionOrderer
{
    public IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(
        IEnumerable<Xunit.Abstractions.ITestCollection> testCollections)
        => testCollections.OrderBy(c => c.DisplayName.Contains("Markdown", StringComparison.Ordinal) ? 1 : 0);
}
