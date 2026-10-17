using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;
using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Operator: a top-bar "Shut down" that checkpoints every active agent then closes gracefully. Asserts the
/// button renders and is wired to ShutdownCommand. (The confirm dialog + live checkpoint-all→close is
/// restart/manual-verified; the command behaviour is covered headlessly in App.Tests.ShutdownTests.)
/// </summary>
[Collection("Avalonia")]
public class TopBarShutdownViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TopBarShutdownViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Top_bar_has_a_shutdown_button_bound_to_the_command()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "sty-shutdown-" + Guid.NewGuid().ToString("N"));
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "test--context.md"), "# test agent context");
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot, new FakePtyLauncher(), new FakeFileWatcher());

                var window = new MainWindow { DataContext = vm, Width = 900, Height = 500 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s == "Shut down");

                var button = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Name == "ShutdownButton");
                Assert.NotNull(button);
                Assert.NotNull(button!.Command);   // wired to ShutdownCommand

                window.Close();
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
        });
    }
    [Fact]
    public Task Window_close_waits_for_agent_disposal_before_reporting_closed()
        => _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "styloagent-close-wait-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "saved-context"));
            await File.WriteAllTextAsync(Path.Combine(root, "saved-context", "test-context.md"), "checkpoint");
            var pty = new DelayedDisposePty();
            using var vm = await MainWindowViewModel.InitializeAsync(root, new DelayedLauncher(pty), new FakeFileWatcher());
            var window = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            try
            {
                for (var i = 0; i < 50 && vm.Pane?.CurrentPty is null; i++) await Task.Delay(10);
                Assert.NotNull(vm.Pane?.CurrentPty);
                window.Close();
                await pty.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(closed.Task.IsCompleted);
                Assert.True(window.IsVisible);
                pty.AllowDispose.TrySetResult();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(vm.IsStopped);
                Assert.False(window.IsVisible);
            }
            finally
            {
                pty.AllowDispose.TrySetResult();
                await vm.StopAsync();
                window.Close();
                Directory.Delete(root, recursive: true);
            }
        });

    private sealed class DelayedLauncher(IPtySession pty) : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
            => Task.FromResult(pty);
    }

    private sealed class DelayedDisposePty : IPtySession
    {
        public TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0067
        public event Action<string>? Output;
        public event Action? Exited;
#pragma warning restore CS0067
        public bool IsIdle => true;
        public void Resize(int cols, int rows) { }
        public ValueTask WriteAsync(string text, CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); await AllowDispose.Task; }
    }
}
