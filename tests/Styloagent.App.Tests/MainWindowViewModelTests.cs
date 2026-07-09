using Styloagent.App.ViewModels;

namespace Styloagent.App.Tests;

public class MainWindowViewModelTests : IDisposable
{
    private readonly string _channelRoot;

    public MainWindowViewModelTests()
    {
        // Set up a minimal channel directory with one agent context file.
        _channelRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(_channelRoot, "saved-context");
        Directory.CreateDirectory(savedContext);
        File.WriteAllText(Path.Combine(savedContext, "foss-context.md"), "# FOSS context");
    }

    public void Dispose()
    {
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public async Task Initialize_WithOneContextFile_ExposesOnePaneForFirstAgent()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        Assert.NotNull(vm.Pane);
    }

    [Fact]
    public async Task Initialize_Pane_HasCorrectPrefix()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        // DisplayName defaults to prefix with trailing '-' trimmed.
        Assert.Equal("foss", vm.Pane!.DisplayName);
    }

    [Fact]
    public async Task Initialize_Pane_HasBorderColorHex()
    {
        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher());

        // Colour is derived deterministically from prefix and must be a hex string.
        Assert.NotNull(vm.Pane!.BorderColorHex);
        Assert.StartsWith("#", vm.Pane.BorderColorHex);
    }

    [Fact]
    public async Task Initialize_WithStoredPresentation_UsesStoredDisplayName()
    {
        // Write a presentation sidecar with a custom name for foss-.
        var presentationPath = Path.Combine(_channelRoot, "presentation.yaml");
        var store = new Styloagent.App.Config.PresentationStore();
        await store.SaveAsync(presentationPath,
        [
            new("foss-", "My FOSS Agent", "#FF0000"),
        ]);

        var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot,
            new FakeLauncher(),
            new FakeWatcher(),
            presentationPath: presentationPath);

        Assert.Equal("My FOSS Agent", vm.Pane!.DisplayName);
        Assert.Equal("#FF0000", vm.Pane.BorderColorHex);
    }

    [Fact]
    public async Task Initialize_EmptyChannel_PaneIsNull()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        // Don't create the saved-context directory.
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                emptyRoot,
                new FakeLauncher(),
                new FakeWatcher());

            Assert.Null(vm.Pane);
        }
        finally
        {
            if (Directory.Exists(emptyRoot))
                Directory.Delete(emptyRoot, recursive: true);
        }
    }
}
