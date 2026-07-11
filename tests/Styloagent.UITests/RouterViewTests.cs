using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Router;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class RouterViewTests : IDisposable
{
    private readonly HeadlessAvaloniaFixture _fx;
    private readonly string _routerRoot;

    public RouterViewTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _routerRoot = Path.Combine(Path.GetTempPath(), "routerview-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        // Write a resource.yaml so the env/account is discovered
        var resDir = Path.Combine(_routerRoot, "prod", "accounts", "deploy-key");
        Directory.CreateDirectory(resDir);
        File.WriteAllText(Path.Combine(resDir, "resource.yaml"), "capacity: 1\nleaseTtl: 10m\n");
        // Write a live grant (recent mtime → live)
        RouterWriter.WriteGrant(_routerRoot, "prod", ResourceKind.Account, "deploy-key",
            "agent-1-", now - TimeSpan.FromSeconds(30), now + TimeSpan.FromMinutes(10), now - TimeSpan.FromSeconds(60));
        // Write a queued claim (different prefix so it's not granted)
        RouterClient.DropClaim(_routerRoot, "prod", "deploy-key", "agent-2-", "deploy to prod", now);
    }

    public void Dispose()
    {
        if (Directory.Exists(_routerRoot))
            Directory.Delete(_routerRoot, recursive: true);
    }

    [Fact]
    public Task RouterView_renders_resources_and_holder()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new RouterViewModel(_routerRoot);
            vm.Refresh();
            var view = new RouterView { DataContext = vm };
            var window = new Window { Width = 400, Height = 600, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            Assert.Contains(texts, s => s.Contains("deploy-key"));
            Assert.Contains(texts, s => s.Contains("agent-1-"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-router.png");
            window.Close();
        });
    }
}
