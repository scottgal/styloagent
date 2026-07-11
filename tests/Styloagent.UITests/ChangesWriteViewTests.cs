using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Git;
using Styloagent.Git;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class ChangesWriteViewTests(HeadlessAvaloniaFixture fx) : IDisposable
{
    // ── fake collaborators ───────────────────────────────────────────────────

    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 0, 0, false,
                new[]
                {
                    new GitChange("staged.txt",   GitChangeKind.Modified, Staged: true,  Unstaged: false),
                    new GitChange("unstaged.txt", GitChangeKind.Added,    Staged: false, Unstaged: true),
                })));

        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeDiff : IGitDiff
    {
        public Task<GitResult<FileDiff>> GetDiffAsync(string w, string path, bool staged, CancellationToken ct = default)
            => Task.FromResult(GitResult<FileDiff>.Success(new FileDiff(path, 1, 0, false,
                new[] { new DiffLine(DiffLineKind.Added, "+ hello", 0, 1) })));
    }

    private sealed class FakeWrite : IGitWrite
    {
        public Task<GitResult> StageAsync(string w, string p, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> UnstageAsync(string w, string p, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> CommitAsync(string w, string m, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> PushAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> PullAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());
    }

    public void Dispose() { }

    // ── render test ──────────────────────────────────────────────────────────

    [Fact]
    public Task ChangesView_renders_staged_unstaged_sections_and_action_buttons()
    {
        return fx.DispatchAsync(async () =>
        {
            var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
            await vm.LoadAsync("/wt");

            var view   = new ChangesView { DataContext = vm };
            var window = new Window { Width = 420, Height = 700, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            // Section headers
            Assert.Contains(texts, s => s.Contains("Unstaged"));
            Assert.Contains(texts, s => s.Contains("Staged"));

            // File paths
            Assert.Contains(texts, s => s.Contains("unstaged.txt"));
            Assert.Contains(texts, s => s.Contains("staged.txt"));

            // Stage button exists
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Select(b => b.Content?.ToString() ?? string.Empty)
                .ToList();
            Assert.Contains(buttons, b => b == "Stage");
            Assert.Contains(buttons, b => b == "Unstage");
            Assert.Contains(buttons, b => b == "Commit");
            Assert.Contains(buttons, b => b == "Push");
            Assert.Contains(buttons, b => b == "Pull");

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-changes.png");
            window.Close();
        });
    }
}
