using System.Diagnostics;
using Styloagent.Core.Git;
using Xunit;

public class GitServiceIntegrationTests
{
    // Skip cleanly when git is unavailable so CI without git stays green.
    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void Run(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task AddWorktree_then_status_then_merge_and_remove()
    {
        if (!GitAvailable()) return;

        var repo = Path.Combine(Path.GetTempPath(), "gitsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main");
            Run(repo, "config user.email t@t.t");
            Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
            Run(repo, "add -A");
            Run(repo, "commit -m init");

            var git = new Styloagent.Git.GitService();
            var wt = Path.Combine(repo, ".worktrees", "foss");

            var add = await git.AddWorktreeAsync(repo, wt, "agent/foss");
            Assert.True(add.Ok, add.Error);
            Assert.True(Directory.Exists(wt));

            File.WriteAllText(Path.Combine(wt, "b.txt"), "two");
            Run(wt, "add -A");
            Run(wt, "commit -m work");

            var status = await git.GetStatusAsync(wt);
            Assert.True(status.Ok);
            Assert.False(status.Value!.IsDirty);

            var merge = await git.MergeNoFfAsync(repo, "agent/foss", "main");
            Assert.True(merge.Ok, merge.Error);
            Assert.True(File.Exists(Path.Combine(repo, "b.txt")));

            var remove = await git.RemoveWorktreeAsync(repo, wt);
            Assert.True(remove.Ok, remove.Error);
            Assert.False(Directory.Exists(wt));
        }
        finally { TryDeleteRepo(repo); }
    }

    [Fact]
    public async Task Push_to_a_local_bare_remote()
    {
        if (!GitAvailable()) return;
        var root = Path.Combine(Path.GetTempPath(), "gitpush-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "remote.git");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(bare); Directory.CreateDirectory(work);
        try
        {
            Run(bare, "init --bare -b main");
            Run(work, "init -b main"); Run(work, "config user.email t@t.t"); Run(work, "config user.name t");
            File.WriteAllText(Path.Combine(work, "a.txt"), "one\n"); Run(work, "add -A"); Run(work, "commit -m init");
            Run(work, $"remote add origin \"{bare}\"");
            Run(work, "push -u origin main");
            File.WriteAllText(Path.Combine(work, "a.txt"), "two\n"); Run(work, "commit -am second");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.PushAsync(work)).Ok, "push should succeed to the configured upstream");
        }
        finally { TryDeleteRepo(root); }
    }

    [Fact]
    public async Task GetDiff_reports_an_unstaged_change()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitdiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            var result = await git.GetDiffAsync(repo, "a.txt", staged: false);
            Assert.True(result.Ok, result.Error);
            Assert.Contains(result.Value!.Lines, l => l.Content == "two");
        }
        finally { TryDeleteRepo(repo); }
    }

    [Fact]
    public async Task Stage_commit_round_trip()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.StageAsync(repo, "a.txt")).Ok);

            var staged = await git.GetStatusAsync(repo);
            Assert.Contains(staged.Value!.Changes, c => c.Path == "a.txt" && c.Staged);

            Assert.True((await git.CommitAsync(repo, "line 1\nline 2")).Ok);   // multiline message
            var afterCommit = await git.GetStatusAsync(repo);
            Assert.False(afterCommit.Value!.IsDirty);
        }
        finally { TryDeleteRepo(repo); }
    }

    private static void TryDeleteRepo(string repo)
    {
        try { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); } catch { }
    }
}
