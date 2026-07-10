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

            async Task OpenProjectAsync(string root, Window? welcomeWindow)
            {
                try
                {
                    var cfg = ProjectScaffolder.Ensure(root);
                    await recents.AddAsync(recentsPath, root);
                    var vm = await MainWindowViewModel.InitializeAsync(
                        cfg.ChannelRoot,
                        new PortaPtyLauncher(),
                        new FileSystemFileWatcher(),
                        gitReader: null,
                        repoRoot: cfg.Root,
                        overviewSystemPromptPath: cfg.SystemPromptPath);
                    vm.AttachProject(cfg);
                    await vm.StartFleetServerAsync();

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var cockpit = new MainWindow { DataContext = vm };
                        desktop.MainWindow = cockpit;
                        cockpit.Show();
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
                placeholder.Show();
                _ = OpenProjectAsync(repoEnv, welcomeWindow: null);
            }
            else
            {
                var welcomeWindow = new Window { Title = "Styloagent", Width = 520, Height = 380 };
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
}
