using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Docs;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// The doc library as a lazy repo browser (operator, 2026-07-18): collapsed sections that load on expand,
/// a sort-by control, and an in-pane filename search — the fix for "show everything is SLOW".
/// </summary>
[Collection("Avalonia")]
public class DocLibraryViewTests : IDisposable
{
    private readonly HeadlessAvaloniaFixture _fx;
    private readonly string _repoRoot;
    private readonly string _channelRoot;

    public DocLibraryViewTests(HeadlessAvaloniaFixture fx)
    {
        _fx = fx;
        _repoRoot = Path.Combine(Path.GetTempPath(), "doclibview-repo-" + Guid.NewGuid().ToString("N"));
        _channelRoot = Path.Combine(Path.GetTempPath(), "doclibview-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_channelRoot);
        File.WriteAllText(Path.Combine(_repoRoot, "readme.md"), "# Readme\n\nRepo document.");
        File.WriteAllText(Path.Combine(_channelRoot, "notes.md"), "# Notes\n\nChannel document.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_channelRoot)) Directory.Delete(_channelRoot, recursive: true);
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        for (int w = 0; w < timeoutMs && !cond(); w += 10) await Task.Delay(10);
    }

    /// <summary>A lister that flags if it ever ran on the UI thread (it must not — folder I/O is off-thread).</summary>
    private sealed class CapturingLister : IDocDirLister
    {
        private readonly Dictionary<string, List<DocDirItem>> _dirs;
        public bool AnyOnUiThread;
        public CapturingLister(Dictionary<string, List<DocDirItem>> dirs) => _dirs = dirs;
        public IReadOnlyList<DocDirItem> List(string dir)
        {
            if (Dispatcher.UIThread.CheckAccess()) AnyOnUiThread = true;
            return _dirs.TryGetValue(dir, out var v) ? v : new List<DocDirItem>();
        }
    }

    [Fact]
    public Task DocLibraryView_renders_collapsed_sections_with_sort_and_search_controls()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
            var view = new DocLibraryView { DataContext = vm };
            var window = new Window { Width = 320, Height = 600, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, s => s.Contains("repo"));       // top-level section rows (collapsed)
            Assert.Contains(texts, s => s.Contains("channel"));

            // Sort-by control + filename search box are present.
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), _ => true);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), _ => true);

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-doclibrary.png");
            window.Close();
        });
    }

    [Fact]
    public Task Expanding_a_folder_lists_it_off_the_ui_thread_and_populates_on_it()
    {
        return _fx.DispatchAsync(async () =>
        {
            const string repo = "/fake/repo";
            var lister = new CapturingLister(new()
            {
                [repo] = new()
                {
                    new DocDirItem("a.md", repo + "/a.md", false, DateTimeOffset.UnixEpoch),
                    new DocDirItem("b.md", repo + "/b.md", false, DateTimeOffset.UnixEpoch),
                },
            });

            var vm = new DocLibraryViewModel(repo, null, _ => { }, lister: lister);
            var repoSection = vm.Roots.Single(r => r.Name == "repo");
            Assert.True(repoSection.Children[0].IsPlaceholder);   // lazy until expanded

            repoSection.IsExpanded = true;                        // triggers the off-thread load
            await WaitUntil(() => repoSection.Children.Count == 2);

            Assert.False(lister.AnyOnUiThread);                   // the (blocking) listing never ran on the UI thread
            Assert.Equal("a.md,b.md", string.Join(",", repoSection.Children.Select(c => c.Name)));
        });
    }

    [Fact]
    public Task Open_reads_the_file_off_the_ui_thread_and_opens_on_it()
    {
        return _fx.DispatchAsync(async () =>
        {
            bool buildOnUiThread = true;
            bool openOnUiThread = false;

            Func<DocEntry, MarkdownDocumentViewModel> build = e =>
            {
                buildOnUiThread = Dispatcher.UIThread.CheckAccess();
                return new MarkdownDocumentViewModel(e.Title, e.FullPath);
            };
            Action<MarkdownDocumentViewModel> open = _ => openOnUiThread = Dispatcher.UIThread.CheckAccess();

            var entry = new DocEntry("readme.md", Path.Combine(_repoRoot, "readme.md"), DocSource.Repo, "readme.md");
            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, open, buildDoc: build);

            await vm.OpenDocAsync(entry);

            Assert.False(buildOnUiThread);   // file read happened OFF the UI thread
            Assert.True(openOnUiThread);      // the dock open happened ON the UI thread
        });
    }
}
