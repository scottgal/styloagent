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

    private static void TryDeleteRepo(string repo)
    {
        try { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); } catch { }
    }
}
