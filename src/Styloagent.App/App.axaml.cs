using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
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
            var channelRoot = Environment.GetEnvironmentVariable("STYLOAGENT_CHANNEL")
                ?? "/tmp/agent-channel";

            // Show the window immediately so the user sees it right away.
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Show();

            // Initialise the view-model asynchronously; if seeding fails or finds
            // nothing the factory still produces an empty shell (handled in InitializeAsync).
            _ = Task.Run(async () =>
            {
                try
                {
                    var vm = await MainWindowViewModel.InitializeAsync(
                        channelRoot,
                        new PortaPtyLauncher(),
                        new FileSystemFileWatcher());

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        window.DataContext = vm;
                    });
                }
                catch (Exception ex)
                {
                    // Log to trace; the window stays open showing an empty shell.
                    System.Diagnostics.Trace.WriteLine($"[Styloagent] Init failed: {ex}");
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
