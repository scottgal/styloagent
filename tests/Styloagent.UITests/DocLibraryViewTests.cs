using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Docs;
using Xunit;

namespace Styloagent.UITests;

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
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public Task DocLibraryView_renders_group_headers_and_doc_buttons()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { });
            await vm.RefreshAsync();   // enumeration is now async — settle it deterministically before asserting
            var view = new DocLibraryView { DataContext = vm };
            var window = new Window { Width = 320, Height = 600, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            // Group header text should be visible
            Assert.Contains(texts, s => s.Contains("repo"));
            Assert.Contains(texts, s => s.Contains("channel"));

            // At least one doc entry button should have materialized
            Assert.Contains(texts, s => s.Contains("readme.md") || s.Contains("notes.md"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-doclibrary.png");
            window.Close();
        });
    }

    // BUG 2 (async doc library): the enumeration is a recursive directory walk — it must run OFF the UI
    // thread so a large tree can NEVER freeze the render thread. Assert the reader ran on a background
    // thread and the observable collections still populated (marshalled back to the UI thread).
    [Fact]
    public Task DocLibrary_enumeration_runs_off_the_ui_thread()
    {
        return _fx.DispatchAsync(async () =>
        {
            int uiThreadId = Environment.CurrentManagedThreadId;
            int readThreadId = uiThreadId;
            bool readOnUiThread = true;

            Func<IReadOnlyList<DocEntry>> read = () =>
            {
                readThreadId = Environment.CurrentManagedThreadId;
                readOnUiThread = Dispatcher.UIThread.CheckAccess();
                return DocLibraryReader.Read(_repoRoot, _channelRoot);
            };

            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, _ => { }, read: read);
            await vm.RefreshAsync();

            Assert.False(readOnUiThread);              // enumeration did NOT run on the UI thread
            Assert.NotEqual(uiThreadId, readThreadId);  // ...it ran on a background thread
            Assert.NotEmpty(vm.Groups);                 // ...and the collections still populated
            Assert.NotEmpty(vm.Roots);
            Assert.False(vm.IsLoading);                 // loading state cleared when done
        });
    }

    // BUG 2 (async open): selecting/opening a doc reads the file (MarkdownDocumentViewModel ctor) — that
    // read must also run OFF the UI thread, while the actual open (dock mutation) marshals back onto it.
    [Fact]
    public Task DocLibrary_open_reads_the_file_off_the_ui_thread_and_opens_on_it()
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

            var entry = new DocEntry("readme.md", Path.Combine(_repoRoot, "readme.md"),
                DocSource.Repo, "readme.md");
            var vm = new DocLibraryViewModel(_repoRoot, _channelRoot, open, buildDoc: build);
            await vm.RefreshAsync();

            await vm.OpenDocAsync(entry);

            Assert.False(buildOnUiThread);   // file read happened OFF the UI thread
            Assert.True(openOnUiThread);      // the dock open happened ON the UI thread
        });
    }
}
