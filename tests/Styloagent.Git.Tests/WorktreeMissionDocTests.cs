using System.Diagnostics;
using Styloagent.Git;
using Xunit;

public class WorktreeMissionDocTests
{
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

    private static string Run(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {p.StandardError.ReadToEnd()}");
        return outp;
    }

    [Fact]
    public async Task Commits_mission_doc_onto_the_worktree_branch()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "missiondoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main");
            Run(repo, "config user.email t@t.t");
            Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, ".gitignore"), ".styloagent/\n");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            Run(repo, "add -A");
            Run(repo, "commit -m init");

            var wt = Path.Combine(repo, ".worktrees", "iso");
            Run(repo, $"worktree add \"{wt}\" -b agent/iso");

            var result = await WorktreeMissionDoc.PlaceAsync(wt, "iso-", "# Mission for iso-\nDo the thing.", commit: true);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal(".styloagent/missions/iso-.md", result.RelativePath);

            // The doc exists in the worktree and holds the content.
            var abs = Path.Combine(wt, ".styloagent", "missions", "iso-.md");
            Assert.True(File.Exists(abs));
            Assert.Contains("Do the thing.", File.ReadAllText(abs));

            // It was committed on the agent branch despite .styloagent/ being gitignored (force-added).
            Assert.Equal("committed", result.Detail);
            var tracked = Run(wt, "ls-files -- .styloagent/missions/iso-.md");
            Assert.Contains(".styloagent/missions/iso-.md", tracked);
            var log = Run(wt, "log --oneline -1");
            Assert.Contains("mission", log);
            // Working tree is clean (the doc is committed, not a dangling untracked/dirty file).
            Assert.Equal(string.Empty, Run(wt, "status --porcelain").Trim());
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Shared_tree_writes_without_committing()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "missiondoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main");
            Run(repo, "config user.email t@t.t");
            Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, ".gitignore"), ".styloagent/\n");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            Run(repo, "add -A");
            Run(repo, "commit -m init");

            var result = await WorktreeMissionDoc.PlaceAsync(repo, "shared-", "brief", commit: false);

            Assert.True(result.Ok, result.Detail);
            Assert.True(File.Exists(Path.Combine(repo, ".styloagent", "missions", "shared-.md")));
            // No commit was made — HEAD is still the single init commit and nothing is tracked under missions/.
            Assert.Equal(string.Empty, Run(repo, "ls-files -- .styloagent/missions/shared-.md").Trim());
            Assert.Single(Run(repo, "log --oneline").Trim().Split('\n'));
        }
        finally { TryDelete(repo); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
