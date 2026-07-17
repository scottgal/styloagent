using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Sessions;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// BUG 3 (headless render): in a multi-repo workspace the Agents roster groups agents under their OWN
/// repo overview and shows repo attribution (a repo header), so cross-repo children no longer look
/// parented under the wrong repo. Renders the real <see cref="AgentsView"/> against a two-repo VM.
/// </summary>
[Collection("Avalonia")]
public class RosterRepoGroupViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public RosterRepoGroupViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NewPtyLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => Task.FromResult<IPtySession>(new FakePtySession());
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static string MakeChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "roster-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "saved-context"));
        File.WriteAllText(Path.Combine(root, "saved-context", "overview-context.md"), "# overview");
        return root;
    }

    [Fact]
    public Task Roster_shows_a_repo_header_for_a_live_opened_second_repo()
    {
        var root = MakeChannel();
        return _fx.DispatchAsync(async () =>
        {
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new NewPtyLauncher(), new NoWatcher());

                // Startup knows only the primary; then a second repo is opened live (register + overview).
                vm.SetReposFromOverviews(new[] { new RepoOverview(
                    "overview-", "/ws/styloagent", "/ws/styloagent/.styloagent/system-prompt.md", 0, "#4CDB6E", true) });
                var second = new RepoOverview(
                    "styloissues-", "/ws/styloissues", "/ws/styloissues/.styloagent/system-prompt.md", 1, "#C77DFF", false);
                vm.AddWorkspaceRepo(second);
                vm.AddRepoOverview(second);

                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 320, Height = 600, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();

                // The multi-repo workspace surfaces a repo header for the live-opened repo (attribution)...
                Assert.Contains(texts, s => s.Contains("styloissues"));
                // ...and both repos' overviews render in the roster.
                Assert.Contains(texts, s => s.Contains("overview"));

                window.Close();
            }
            finally
            {
                vm?.Dispose();
            }
        }).ContinueWith(t =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            t.GetAwaiter().GetResult();
        });
    }
}
