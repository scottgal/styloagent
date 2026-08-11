using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// The Changes panel is the Git view that earns its place: what am I about to commit, what does it change,
/// and can I tag it. It is now the DEFAULT tab (History — the commit graph — is expensive to build and was
/// both first and always-loaded). These tests pin the affordances so the panel can't quietly stop showing
/// them, and render it so the layout is checked, not assumed.
/// </summary>
[Collection("Avalonia")]
public class ChangesPanelUsabilityTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ChangesPanelUsabilityTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 2, 0, false, new[]
            {
                new GitChange("src/Styloagent.Terminal/TerminalControl.axaml.cs", GitChangeKind.Modified, Staged: true,  Unstaged: false),
                new GitChange("src/Styloagent.Core/Mcp/CodexModelDiscovery.cs",   GitChangeKind.Added,    Staged: true,  Unstaged: false),
                new GitChange("docs/manual/images/cockpit.png",                   GitChangeKind.Modified, Staged: false, Unstaged: true),
            })));
        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeDiff : IGitDiff
    {
        public Task<GitResult<FileDiff>> GetDiffAsync(string w, string path, bool staged, CancellationToken ct = default)
            => Task.FromResult(GitResult<FileDiff>.Success(new FileDiff(path, 2, 1, false, new[]
            {
                new DiffLine(DiffLineKind.Context, "    private void RunCoalescedRebuild()", 118, 118),
                new DiffLine(DiffLineKind.Deleted, "        RebuildRows();", 119, 0),
                new DiffLine(DiffLineKind.Added,   "        if (!IsOnScreen) { Register(this); return; }", 0, 119),
                new DiffLine(DiffLineKind.Added,   "        RebuildRows();", 0, 120),
            })));
    }

    private sealed class FakeWrite : IGitWrite
    {
        public Task<GitResult> StageAsync(string w, string p, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> UnstageAsync(string w, string p, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> CommitAsync(string w, string m, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> PushAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> PullAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeBranch : IGitBranch
    {
        public Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<GitBranch>>.Success(
                new List<GitBranch> { new GitBranch("main", IsCurrent: true) }));
        public Task<GitResult> CreateBranchAsync(string w, string n, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> SwitchBranchAsync(string w, string n, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeStash : IGitStash
    {
        public Task<GitResult> StashAsync(string w, string? m, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> StashPopAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<string>>.Success((IReadOnlyList<string>)Array.Empty<string>()));
    }

    private sealed class FakeTags : IGitTag
    {
        public Task<GitResult<IReadOnlyList<GitTag>>> ListTagsAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<GitTag>>.Success(
                (IReadOnlyList<GitTag>)new List<GitTag> { new GitTag("v1.9.0", "aaa1111"), new GitTag("v1.10.0", "bbb2222") }));
        public Task<GitResult> CreateAnnotatedTagAsync(string w, string name, string message, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> PushTagsAsync(string w, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    private static ChangesViewModel Vm() =>
        new(new FakeGit(), new FakeDiff(), new FakeWrite(), new FakeBranch(), new FakeStash(), new FakeTags());

    [Fact]
    public Task Shows_what_is_about_to_be_committed_its_diff_and_tagging()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = Vm();
            await vm.LoadAsync("/repo");

            // What's about to be committed, split the way the operator thinks about it.
            Assert.Equal(2, vm.StagedFiles.Count);
            Assert.Single(vm.UnstagedFiles);
            Assert.True(vm.CanCommit is false);            // no message yet
            vm.CommitMessage = "perf: stop off-screen terminals rendering";
            Assert.True(vm.CanCommit);                      // staged + message => committable

            // The diff for a selected file.
            await vm.SelectFileAsync(vm.StagedFiles[0]);
            Assert.NotNull(vm.Diff.File);
            Assert.NotEmpty(vm.Diff.File!.Lines);

            // Tagging is live (GitService implements IGitTag, so HasTagging must be true in the real app).
            Assert.True(vm.HasTagging);
            Assert.Equal(2, vm.Tags.Count);
            Assert.Contains("v1.10.0", vm.ExistingTagsText);

            var view = new ChangesView { DataContext = vm };
            var window = new Window { Width = 420, Height = 900, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            // The lists actually materialise rows (a bound-but-empty panel is the failure this catches).
            var staged = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "StagedList");
            var unstaged = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "UnstagedList");
            Assert.Equal(2, staged.ItemCount);
            Assert.Equal(1, unstaged.ItemCount);

            await ScreenshotCapture.CaptureControlAsync(window, view,
                Path.Combine(Path.GetTempPath(), "styloagent-changes-panel.png"));
            window.Close();
        });
    }
}
