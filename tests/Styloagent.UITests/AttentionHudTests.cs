using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class AttentionHudTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AttentionHudTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakeLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => Task.FromResult<IPtySession>(new FakePtySession());
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
    public Task Attention_badge_and_jump_appear_when_an_agent_waits()
    {
        var root = MakeTwoAgentChannel();
        return _fx.DispatchAsync(async () =>
        {
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 360, Content = view };
                window.Show();

                vm.InteractionForTest().RecordInput();  // busy so no auto-reveal churn during the test
                vm.DispatchHookForTest(new HookEvent(vm.FirstHookIdForTest(), "Notification", "permission_prompt", null, null, null));
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s.Contains("waiting"));
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => (b.Content?.ToString() ?? "").Contains("Jump"));
                window.Close();
                vm.Dispose();   // stop idle/debounce timers so a later test's SettleAsync can idle
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }
}
