using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Projects;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string recentsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Styloagent", "recent-projects.yaml");
            var recents = new RecentProjectsStore();

            // Load + apply persisted preferences before any window shows, so the app never flashes the
            // old purple default. The file is tiny; a synchronous load at startup is fine.
            var prefsStore = new PreferencesStore();
            string prefsPath = PreferencesStore.DefaultPath;
            var prefs = prefsStore.Load(prefsPath);
            ThemeApplier.ApplyThemeVariant(this, prefs.LightTheme);
            ThemeApplier.ApplyAccent(this, AccentPalette.Resolve(prefs.Accent));

            async Task OpenProjectAsync(string root, Window? welcomeWindow, MainWindow? existing = null)
            {
                try
                {
                    var cfg = ProjectScaffolder.Ensure(root);
                    await recents.AddAsync(recentsPath, root);
                    var gitSvc = new Styloagent.Git.GitService();
                    var vm = await MainWindowViewModel.InitializeAsync(
                        cfg.ChannelRoot,
                        new PortaPtyLauncher(),
                        new FileSystemFileWatcher(),
                        gitReader: null,
                        repoRoot: cfg.Root,
                        overviewSystemPromptPath: cfg.SystemPromptPath,
                        gitService: gitSvc,
                        gitLog: gitSvc);
                    vm.AttachProject(cfg);
                    vm.AttachPreferences(prefs, prefsStore, prefsPath);
                    await vm.StartFleetServerAsync();

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (existing is not null)
                        {
                            // Fast-path: the placeholder IS a MainWindow — populate it in place so there
                            // is a single window (no swap). This also keeps any attached driver/UI-test
                            // tooling pointed at the real cockpit rather than a discarded placeholder.
                            existing.DataContext = vm;
                        }
                        else
                        {
                            var cockpit = new MainWindow { DataContext = vm };
                            desktop.MainWindow = cockpit;
                            cockpit.Show();
                        }
                        welcomeWindow?.Close();
                    });
                }
                catch (Exception ex)
                {
                    // Log to trace; the window stays open showing an empty shell.
                    System.Diagnostics.Trace.WriteLine($"[Styloagent] Init failed: {ex}");
                }
            }

            // Dispose the VM (and its BusViewModel/FileSystemWatcher) on clean shutdown.
            desktop.ShutdownRequested += (_, _) =>
                (desktop.MainWindow?.DataContext as IDisposable)?.Dispose();

            var repoEnv = Environment.GetEnvironmentVariable("STYLOAGENT_REPO");
            if (!string.IsNullOrWhiteSpace(repoEnv))
            {
                // Fast-path: open directly without showing the Welcome screen.
                var placeholder = new MainWindow();
                desktop.MainWindow = placeholder;
                // Don't Show() here — let the desktop lifetime show MainWindow when its main loop
                // starts. Showing it now would fire Window.Opened before post-Startup hooks (e.g. the
                // UI-test driver) attach, so they'd miss it. The lifetime shows MainWindow for us.
                _ = OpenProjectAsync(repoEnv, welcomeWindow: null, existing: placeholder);
            }
            else
            {
                var welcomeWindow = new Window
                {
                    Title = "Styloagent",
                    Icon = AppIcon(),
                    Width = 520,
                    Height = 380,
                };
                var welcome = new WelcomeViewModel(recents, recentsPath,
                    new StorageFolderPicker(welcomeWindow),
                    root => _ = OpenProjectAsync(root, welcomeWindow));
                welcomeWindow.Content = new WelcomeView { DataContext = welcome };
                desktop.MainWindow = welcomeWindow;
                welcomeWindow.Show();
                _ = welcome.LoadRecentsAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The stylo brand icon as a window icon (loaded from the embedded AvaloniaResource).</summary>
    private static WindowIcon AppIcon()
        => new(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Styloagent.App/icon.png")));
}
