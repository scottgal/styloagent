using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class GitGraphViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public GitGraphViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task GitGraphView_renders_commit_count_header()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fakeLog = new FakeGitLog(
                new Commit { SHA = "aaa111", Subject = "feat: first commit",  Parents = [] },
                new Commit { SHA = "bbb222", Subject = "fix: second commit",  Parents = ["aaa111"] },
                new Commit { SHA = "ccc333", Subject = "chore: third commit", Parents = ["bbb222"] }
            );

            var vm = new GitGraphViewModel(fakeLog);
            await vm.LoadAsync("/fake/worktree");

            var view   = new GitGraphView { DataContext = vm };
            var window = new Window { Width = 360, Height = 600, Content = view };
            window.Show();

            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            Assert.Equal(3, vm.CommitCount);
            Assert.Contains(texts, s => s.Contains("3 commits"));

            await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-gitgraph.png");
            window.Close();
        });
    }

    private sealed class FakeGitLog : IGitLog
    {
        private readonly IReadOnlyList<Commit> _commits;

        public FakeGitLog(params Commit[] commits) => _commits = commits;

        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(
            string worktreePath, int limit = 200, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<Commit>>.Success(_commits));
    }
}
