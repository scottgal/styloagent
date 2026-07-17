using Styloagent.App.Services;

namespace Styloagent.App.Tests;

/// <summary>
/// The live "open repo / second instance" gesture (Bug A): pick a folder → resolve its git root
/// (repo-'s ResolveRepoRootAsync) → confirm it has its own .styloagent/ → open it as a federated
/// instance via <see cref="IRepoInstanceOpener"/> (stubbed until bus-'s per-repo instance seam lands).
/// The orchestration + validation is exercised here with fakes; the federation is the swappable port.
/// </summary>
public class RepoInstanceCoordinatorTests
{
    private sealed class FakePicker : IFolderPicker
    {
        private readonly string? _result;
        public FakePicker(string? result) => _result = result;
        public Task<string?> PickFolderAsync() => Task.FromResult(_result);
    }

    private sealed class RecordingOpener : IRepoInstanceOpener
    {
        public List<string> Opened { get; } = new();
        public Task OpenAsync(string repoRoot, CancellationToken ct = default)
        {
            Opened.Add(repoRoot);
            return Task.CompletedTask;
        }
    }

    private static RepoInstanceCoordinator Coordinator(
        string? picked,
        RecordingOpener opener,
        Func<string, CancellationToken, Task<string?>>? resolve = null,
        Func<string, bool>? isStyloagent = null)
        => new(
            new FakePicker(picked),
            resolve ?? ((p, _) => Task.FromResult<string?>(p)),   // default: the picked path IS the repo root
            opener,
            isStyloagent ?? (_ => true));                          // default: it has a .styloagent/

    [Fact]
    public async Task Cancel_PickingNothing_DoesNotOpen()
    {
        var opener = new RecordingOpener();
        var result = await Coordinator(null, opener).OpenAsync();
        Assert.Equal(OpenRepoInstanceStatus.Cancelled, result.Status);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task NonRepoFolder_IsRejected_WithoutOpening()
    {
        var opener = new RecordingOpener();
        // repo- 's resolver returns null for a non-repo / missing path.
        var result = await Coordinator("/tmp/not-a-repo", opener,
            resolve: (_, _) => Task.FromResult<string?>(null)).OpenAsync();
        Assert.Equal(OpenRepoInstanceStatus.NotARepo, result.Status);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task RepoWithoutStyloagent_IsRejected_WithoutOpening()
    {
        var opener = new RecordingOpener();
        var result = await Coordinator("/repo/plain", opener, isStyloagent: _ => false).OpenAsync();
        Assert.Equal(OpenRepoInstanceStatus.NotStyloagent, result.Status);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task ValidStyloagentRepo_Opens_AtItsCanonicalRoot()
    {
        var opener = new RecordingOpener();
        // Picked a SUBDIR; the resolver canonicalizes to the repo root, which is what we open + key on.
        var result = await Coordinator("/work/stylobot/src", opener,
            resolve: (_, _) => Task.FromResult<string?>("/work/stylobot")).OpenAsync();
        Assert.Equal(OpenRepoInstanceStatus.Opened, result.Status);
        Assert.Equal("/work/stylobot", result.RepoRoot);
        Assert.Equal("/work/stylobot", Assert.Single(opener.Opened));
    }

    [Fact]
    public async Task SameRepo_OpenedTwice_IsDeduped()
    {
        var opener = new RecordingOpener();
        var coord = Coordinator("/work/stylobot", opener);

        var first = await coord.OpenAsync();
        var second = await coord.OpenAsync();

        Assert.Equal(OpenRepoInstanceStatus.Opened, first.Status);
        Assert.Equal(OpenRepoInstanceStatus.AlreadyOpen, second.Status);
        Assert.Single(opener.Opened);                    // opened exactly once
        Assert.Contains("/work/stylobot", coord.OpenRepoRoots);
    }

    [Fact]
    public async Task OpenerThrows_ReportsFailed_AndAllowsRetry()
    {
        var throwing = new ThrowingOpener();
        var coord = new RepoInstanceCoordinator(
            new FakePicker("/work/stylobot"),
            (p, _) => Task.FromResult<string?>(p),
            throwing,
            _ => true);

        var result = await coord.OpenAsync();
        Assert.Equal(OpenRepoInstanceStatus.Failed, result.Status);
        // Not left in the open set — the operator can retry once the cause is fixed.
        Assert.DoesNotContain("/work/stylobot", coord.OpenRepoRoots);
    }

    private sealed class ThrowingOpener : IRepoInstanceOpener
    {
        public Task OpenAsync(string repoRoot, CancellationToken ct = default)
            => throw new InvalidOperationException("federation seam not wired");
    }
}
