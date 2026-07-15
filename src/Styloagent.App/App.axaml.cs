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
using Styloagent.Core.Workspace;
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
            // Give a real claude TUI time to come up before we press Enter on its injected prompt, then
            // press once more as a safety net — otherwise the prompt is typed but never submitted and the
            // agent sits idle. (Zero in tests, where no real claude runs.)
            Styloagent.Core.Sessions.AgentSession.InjectSettleDelay = TimeSpan.FromMilliseconds(2500);
            Styloagent.Core.Sessions.AgentSession.InjectEnterRetryDelay = TimeSpan.FromMilliseconds(2000);

            // Same story for the message-injection fallback: one ESC doesn't reliably break a live claude
            // turn (pause between presses so it can actually die before we re-check idle), and an Enter
            // typed the instant the nudge lands is dropped — so settle before submitting and press once
            // more as a safety net. Otherwise a delivered bus message lingers unsent, needing a manual
            // Enter. (Zero in tests, where no real claude runs.)
            Services.PtyMessageInjector.BreakPollDelay = TimeSpan.FromMilliseconds(150);
            Services.PtyMessageInjector.SubmitSettleDelay = TimeSpan.FromMilliseconds(400);
            Services.PtyMessageInjector.SubmitRetryDelay = TimeSpan.FromMilliseconds(500);

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
                    // A folder with .styloagent-workspace/workspace.yaml is a workspace of N repos; otherwise
                    // it's a single repo (a workspace of one). The primary repo (index 0) anchors on the
                    // existing single-repo flow; every additional repo adds its own overview onto the shared bus.
                    var workspace = new WorkspaceStore().Load(root) ?? WorkspaceConfig.SingleRepo(root);
                    var primary = workspace.Repos[0];
                    var cfg = ProjectScaffolder.Ensure(primary.Path);
                    await recents.AddAsync(recentsPath, root);

                    var overviews = workspace.RepoOverviews();
                    IReadOnlyList<RepoOverview>? extraOverviews = null;
                    string? primaryColorHex = null;
                    if (!workspace.IsSingleRepo)
                    {
                        // Scaffold each additional repo so its .styloagent/system-prompt.md exists, then open it.
                        foreach (var extra in workspace.Repos.Skip(1))
                            ProjectScaffolder.Ensure(extra.Path);
                        primaryColorHex = overviews[0].ColorHex;      // colour the primary by its repo hue too
                        extraOverviews = overviews.Skip(1).ToList();
                    }

                    var gitSvc = new Styloagent.Git.GitService();
                    var vm = await MainWindowViewModel.InitializeAsync(
                        cfg.ChannelRoot,
                        new PortaPtyLauncher(),
                        new FileSystemFileWatcher(),
                        gitReader: null,
                        repoRoot: cfg.Root,
                        overviewSystemPromptPath: cfg.SystemPromptPath,
                        gitService: gitSvc,
                        gitLog: gitSvc,
                        overviewColorHex: primaryColorHex,
                        extraOverviews: extraOverviews);
                    vm.AttachProject(cfg);
                    vm.SetReposFromOverviews(overviews);   // enumerate repos for list_repos + repo-grouped UI
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

            // STYLOAGENT_CHANNEL opens an existing bare channel (agent-channel format: saved-context/,
            // launch-prompts/, inbox/, ...) — but that channel may be LIVE and in use by another fleet, so
            // we copy it into a fresh working repo's .styloagent/channel and open the COPY, never the
            // original. Then the normal repo-open path seeds the fleet from the snapshot.
            var channelEnv = Environment.GetEnvironmentVariable("STYLOAGENT_CHANNEL");
            if (!string.IsNullOrWhiteSpace(channelEnv) && Directory.Exists(channelEnv))
            {
                var name = Path.GetFileName(channelEnv.TrimEnd('/', '\\'));
                var work = Path.Combine(Path.GetTempPath(), "styloagent-channels",
                    name + "-" + Guid.NewGuid().ToString("N")[..8]);
                Styloagent.Core.Channel.ChannelSnapshot.CopyTo(channelEnv, Path.Combine(work, ".styloagent", "channel"));

                var placeholder = new MainWindow();
                desktop.MainWindow = placeholder;
                _ = OpenProjectAsync(work, welcomeWindow: null, existing: placeholder);
                base.OnFrameworkInitializationCompleted();
                return;
            }

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
