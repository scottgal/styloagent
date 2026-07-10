using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class FleetHudTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public FleetHudTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakeLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
        {
            return Task.FromResult<IPtySession>(new FakePtySession());
        }
    }

    private sealed class FakeWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static string MakeTwoAgentChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var ctx = Path.Combine(root, "saved-context");
        Directory.CreateDirectory(ctx);
        File.WriteAllText(Path.Combine(ctx, "alpha-context.md"), "# alpha");
        File.WriteAllText(Path.Combine(ctx, "beta-context.md"), "# beta");
        return root;
    }

    [Fact]
    public Task Roster_shows_fleet_hud_and_pause_toggle()
    {
        var root = MakeTwoAgentChannel();
        return _fx.DispatchAsync(async () =>
        {
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 360, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s.Contains("fleet", StringComparison.Ordinal) && s.Contains('/'));   // HUD present
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => (b.Content?.ToString() ?? "").Contains("Pause", StringComparison.Ordinal));
                window.Close();
            }
            finally
            {
                vm?.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
