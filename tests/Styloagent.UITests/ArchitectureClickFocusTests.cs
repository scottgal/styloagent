using Styloagent.App.ViewModels;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Diagrams;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Proves the C4 architecture diagram is a navigation surface: clicking a component (surfaced as a
/// C4 element id) focuses its owning agent's pane. Exercises MarkdownDocumentViewModel.ComponentClicked
/// → MainWindowViewModel.FocusAgentByComponentId end-to-end.
/// </summary>
[Collection("Avalonia")]
public class ArchitectureClickFocusTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ArchitectureClickFocusTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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
        var root = Path.Combine(Path.GetTempPath(), "arch-click-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "saved-context"));
        File.WriteAllText(Path.Combine(root, "saved-context", "overview-context.md"), "# overview");
        return root;
    }

    [Fact]
    public Task Clicking_a_component_focuses_the_owning_agent()
    {
        var root = MakeChannel();
        return _fx.DispatchAsync(async () =>
        {
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new NewPtyLauncher(), new NoWatcher());
                var pane = vm.Panes[0];
                var componentId = SystemMapGenerator.Id(pane.Prefix);

                var doc = MarkdownDocumentViewModel.FromMarkdown("Architecture", "# arch");
                vm.OpenMarkdownDocument(doc);

                vm.SelectedPane = null;

                doc.RaiseComponentClicked("no-such-component");
                Assert.Null(vm.SelectedPane);                 // unknown component → no focus change

                doc.RaiseComponentClicked(componentId);
                Assert.Same(pane, vm.SelectedPane);           // owning agent focused
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
