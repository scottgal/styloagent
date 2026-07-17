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

    [Fact]
    public async Task Branch_create_switch_and_list()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitbranch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.CreateBranchAsync(repo, "feature")).Ok);        // creates + switches
            var list = await git.ListBranchesAsync(repo);
            Assert.True(list.Ok);
            Assert.Contains(list.Value!, b => b.Name == "feature" && b.IsCurrent);
            Assert.Contains(list.Value!, b => b.Name == "main" && !b.IsCurrent);
            Assert.True((await git.SwitchBranchAsync(repo, "main")).Ok);
            var after = await git.ListBranchesAsync(repo);
            Assert.Contains(after.Value!, b => b.Name == "main" && b.IsCurrent);
        }
        finally { TryDeleteRepo(repo); }
    }

    [Fact]
    public async Task Stash_save_list_and_pop()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitstash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");

            var git = new Styloagent.Git.GitService();
            Assert.True((await git.StashAsync(repo, "wip")).Ok);
            Assert.False((await git.GetStatusAsync(repo)).Value!.IsDirty);
            var list = await git.ListStashesAsync(repo);
            Assert.True(list.Ok); Assert.Single(list.Value!);
            Assert.True((await git.StashPopAsync(repo)).Ok);
            Assert.True((await git.GetStatusAsync(repo)).Value!.IsDirty);
        }
        finally { TryDeleteRepo(repo); }
    }

    /// <summary>
    /// Regression for the cockpit-freeze class: git subprocesses forking on the UI thread. Every git
    /// command routes through <c>GitService.RunAsync</c>, whose <c>Process.Start</c> (fork/exec) once ran
    /// in the method's SYNCHRONOUS prefix — i.e. on whatever thread called it, which for the git panel is
    /// the Avalonia dispatcher (RefreshGitPanelFor → the loaders). Under repo churn that fork storm
    /// contended with text-shaping on the libmalloc fork-lock and froze the app. The fork must run OFF the
    /// caller thread (hopped to the pool), so it is observed on a DIFFERENT thread than the caller.
    /// </summary>
    [Fact]
    public void RunAsync_forks_off_the_caller_thread()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitfork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n"); Run(repo, "add -A"); Run(repo, "commit -m init");

            var box = new System.Runtime.CompilerServices.StrongBox<int>(-1);
            int callerThreadId = 0;
            // A dedicated thread stands in for the UI/dispatcher thread: its managed id is unique and never
            // reused by the thread pool, so a pool-thread fork is provably a different thread. The AsyncLocal
            // probe flows into RunAsync's Task.Run and records the thread the fork actually ran on.
            var caller = new System.Threading.Thread(() =>
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                Styloagent.Git.GitService.ForkThreadProbe.Value = box;
                _ = new Styloagent.Git.GitService().GetStatusAsync(repo).GetAwaiter().GetResult();
            }) { IsBackground = true };
            caller.Start();
            Assert.True(caller.Join(TimeSpan.FromSeconds(30)), "git status did not complete");

            Assert.NotEqual(-1, box.Value);                // the fork ran and was observed
            Assert.NotEqual(callerThreadId, box.Value);    // ...but NOT on the caller (UI-stand-in) thread
        }
        finally { TryDeleteRepo(repo); }
    }

    // ── ResolveRepoRootAsync — the "operator picked a folder → which repo is this?" read that backs
    //    the open-instance gesture for a NON-primary repo (a second federated instance). ──────────────

    [Fact]
    public async Task ResolveRepoRoot_normalizes_a_subdir_to_the_repo_root()
    {
        if (!GitAvailable()) return;
        var repo = Path.Combine(Path.GetTempPath(), "gitroot-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(repo, "src", "nested");
        Directory.CreateDirectory(sub);
        try
        {
            Run(repo, "init -b main"); Run(repo, "config user.email t@t.t"); Run(repo, "config user.name t");

            var git = new Styloagent.Git.GitService();
            var fromRoot = await git.ResolveRepoRootAsync(repo);
            var fromSub = await git.ResolveRepoRootAsync(sub);

            Assert.NotNull(fromRoot);
            Assert.NotNull(fromSub);
            // A subdir resolves to the SAME canonical root as the top — the property the (repo,prefix)
            // instance key relies on, so two panes opened from different folders of one repo coincide.
            Assert.Equal(fromRoot, fromSub);
            // ...and that root IS this repo. Compare the leaf segment: it is symlink-invariant, unlike the
            // absolute path (macOS resolves the temp dir's /var → /private/var under --show-toplevel).
            Assert.Equal(Path.GetFileName(repo), Path.GetFileName(fromRoot!.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.True(Directory.Exists(fromRoot));
        }
        finally { TryDeleteRepo(repo); }
    }

    [Fact]
    public async Task ResolveRepoRoot_of_a_non_repo_directory_is_null()
    {
        if (!GitAvailable()) return;
        var dir = Path.Combine(Path.GetTempPath(), "notrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var git = new Styloagent.Git.GitService();
            Assert.Null(await git.ResolveRepoRootAsync(dir));   // not a repo → null, never throws
        }
        finally { TryDeleteRepo(dir); }
    }

    [Fact]
    public async Task ResolveRepoRoot_of_a_missing_path_is_null()
    {
        // Guard path: no directory, so it must short-circuit to null without forking git.
        var git = new Styloagent.Git.GitService();
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Null(await git.ResolveRepoRootAsync(missing));
    }

    private static void TryDeleteRepo(string repo)
    {
        try { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); } catch { }
    }
}
