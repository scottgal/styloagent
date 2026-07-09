using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.App.Config;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Seeding;
using Styloagent.Core.Sessions;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Root view-model for the main window.  On construction it seeds the channel,
/// loads presentation data, and exposes the first agent as <see cref="Pane"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private AgentPaneViewModel? _pane;

    // private ctor — callers must use InitializeAsync.
    private MainWindowViewModel() { }

    /// <summary>
    /// Factory method: seeds the channel from <paramref name="channelRoot"/>, loads
    /// (or creates) the presentation sidecar, wires up the first agent pane, and
    /// returns a fully-initialised view-model.
    /// </summary>
    public static async Task<MainWindowViewModel> InitializeAsync(
        string channelRoot,
        IPtyLauncher launcher,
        IFileWatcher watcher,
        string? presentationPath = null,
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();

        var seeder = new ChannelManifestSeeder();
        var entries = await seeder.SeedAsync(channelRoot, new Dictionary<string, string>());

        if (entries.Count == 0)
            return vm;

        var first = entries[0];

        // Load or derive the presentation.
        var store = new PresentationStore();
        AgentPresentation? presentation = null;

        if (presentationPath != null && File.Exists(presentationPath))
        {
            var all = await store.LoadAsync(presentationPath);
            presentation = all.FirstOrDefault(p => p.Prefix == first.Prefix);
        }

        presentation ??= new AgentPresentation(
            Prefix: first.Prefix,
            DisplayName: first.Prefix.TrimEnd('-'),
            BorderColorHex: PresentationStore.DefaultColorFor(first.Prefix));

        var session = new AgentSession(first, launcher, watcher);

        vm.Pane = new AgentPaneViewModel(
            session,
            first,
            presentation.DisplayName,
            presentation.BorderColorHex);

        return vm;
    }
}
