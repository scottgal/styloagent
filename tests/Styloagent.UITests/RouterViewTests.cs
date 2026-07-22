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
    private readonly string _environmentsRoot;
    private readonly string _browserRoot;

    public RouterViewTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _routerRoot = Path.Combine(Path.GetTempPath(), "routerview-" + Guid.NewGuid().ToString("N"));
        _environmentsRoot = Path.Combine(Path.GetTempPath(), "environmentview-" + Guid.NewGuid().ToString("N"));
        _browserRoot = Path.Combine(Path.GetTempPath(), "browserview-" + Guid.NewGuid().ToString("N"));
        Styloagent.Core.Environments.EnvironmentRegistry.Create(
            _environmentsRoot, "staging", "Staging", "non-production", "deploy-");
        new Styloagent.Core.Browser.BrowserJobStore(_browserRoot).Create(
            "test-", "staging", Styloagent.Core.Browser.BrowserRunMode.Observe, "capture", "/", null,
            false, null, DateTimeOffset.UtcNow);
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
        if (Directory.Exists(_environmentsRoot))
            Directory.Delete(_environmentsRoot, recursive: true);
        if (Directory.Exists(_browserRoot))
            Directory.Delete(_browserRoot, recursive: true);
    }

    [Fact]
    public Task RouterView_renders_resources_and_holder()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new RouterViewModel(_routerRoot, _environmentsRoot, _browserRoot);
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
            Assert.Contains(texts, s => s.Contains("Staging"));
            Assert.Contains(texts, s => s.Contains("owner: deploy-"));
            Assert.Contains(texts, s => s.Contains("Playwright: 0 active · 1 pending"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-router.png");
            window.Close();
        });
    }
}
