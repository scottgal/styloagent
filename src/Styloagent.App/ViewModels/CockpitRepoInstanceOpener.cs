using Dock.Model.Mvvm.Controls;
using Styloagent.App.Services;
using Styloagent.Core.Channel;

namespace Styloagent.App.ViewModels;

/// <summary>
/// The real <see cref="IRepoInstanceOpener"/>: resolves a repoRoot to its OWN federated channel via bus-'s
/// blessed <see cref="RepoChannelResolver"/> (so the instance's channel can't drift from routing), then
/// hands the <see cref="RepoChannel"/> to the shell to launch as a fully independent instance (own hooks,
/// own delivery coordinator, own bus pane + overview agent). Cross-repo <c>send_message(repo:)</c> between
/// instances is the follow-on co-land with bus-; it rides the same canonical-repoRoot key.
/// </summary>
public sealed class CockpitRepoInstanceOpener : IRepoInstanceOpener
{
    private readonly RepoChannelResolver _resolver;
    private readonly Func<RepoChannel, Task> _surface;

    public CockpitRepoInstanceOpener(RepoChannelResolver resolver, Func<RepoChannel, Task> surface)
    {
        _resolver = resolver;
        _surface = surface;
    }

    public async Task OpenAsync(string repoRoot, CancellationToken ct = default)
    {
        var channel = await _resolver.ResolveAsync(repoRoot, ct).ConfigureAwait(false);
        await _surface(channel).ConfigureAwait(false);
    }
}

/// <summary>
/// A federated repo instance's bus feed, hosted as a document tab: it IS a Dock <see cref="Document"/>, and
/// its <see cref="Bus"/> renders through the App.axaml <c>RepoBusDocumentViewModel → BusView</c> template.
/// Titled by the repo's display name; keyed by its canonical <see cref="RepoRoot"/>.
/// </summary>
public sealed class RepoBusDocumentViewModel : Document
{
    /// <summary>The canonical repo root — the federation/dedupe key.</summary>
    public string RepoRoot { get; }

    /// <summary>The live bus feed watching this repo instance's own channel.</summary>
    public BusViewModel Bus { get; }

    public RepoBusDocumentViewModel(string repoRoot, string repoName, BusViewModel bus)
    {
        RepoRoot = repoRoot;
        Bus = bus;
        Id = "RepoBus-" + repoRoot;
        Title = string.IsNullOrWhiteSpace(repoName) ? "repo · bus" : $"{repoName} · bus";
        CanFloat = true;
    }
}
