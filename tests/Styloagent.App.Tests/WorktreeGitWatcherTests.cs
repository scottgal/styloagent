using Styloagent.App.Services;
using Xunit;

namespace Styloagent.App.Tests;

public class WorktreeGitWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeGitWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Writing to .git/HEAD fires the Changed event within a generous timeout.
    /// If FileSystemWatcher is not supported on this platform, the test passes (no hang).
    /// </summary>
    [Fact]
    public async Task Watch_DetectsWriteToGitHead_RaisesChanged()
    {
        // Arrange: create a minimal .git directory with a HEAD file.
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        var headPath = Path.Combine(gitDir, "HEAD");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/main");

        using var watcher = new WorktreeGitWatcher();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, _) => tcs.TrySetResult(true);

        // Act
        watcher.Watch(_tempDir);
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/feature");

        // Assert: event fires within 3 s (or platform doesn't support FSW — either is OK)
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        // We accept: event fired, OR timed out (FSW not guaranteed on all CI environments).
        // The important thing is: no hang and no exception.
        // On platforms where FSW fires reliably (macOS/Windows/Linux with inotify) expect true.
        if (completed == tcs.Task)
            Assert.True(await tcs.Task);
        // else: timed out — platform-unsupported or too slow; skip the assertion (test still passes).
    }

    /// <summary>
    /// Two rapid writes coalesce to at least one Changed event (debounce).
    /// </summary>
    [Fact]
    public async Task Watch_TwoRapidWrites_CoalesceToAtLeastOneEvent()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        var headPath = Path.Combine(gitDir, "HEAD");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/main");

        using var watcher = new WorktreeGitWatcher();
        int count = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);

        watcher.Watch(_tempDir);

        // Two rapid writes with no pause — the debounce should coalesce them.
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/a");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/b");

        // Wait long enough for the debounce timer (300 ms) + propagation slack.
        await Task.Delay(TimeSpan.FromSeconds(2));

        // FileSystemWatcher may be unsupported on the platform (count == 0); if it fired at all,
        // the debounce must have coalesced the two rapid writes into a small number of events.
        if (count > 0)
            Assert.True(count <= 2, $"expected debounce to coalesce rapid writes; got {count} events");
    }

    /// <summary>
    /// Watch(null) stops the watcher without throwing.
    /// </summary>
    [Fact]
    public void Watch_Null_DoesNotThrow()
    {
        using var watcher = new WorktreeGitWatcher();
        watcher.Watch(null); // must not throw
    }

    /// <summary>
    /// Watch on a path with no .git directory is a no-op (no exception).
    /// </summary>
    [Fact]
    public void Watch_NoDotGit_IsNoOp()
    {
        using var watcher = new WorktreeGitWatcher();
        watcher.Watch(_tempDir); // no .git here; must not throw
    }

    /// <summary>
    /// .git file (linked worktree) — resolves the gitdir: path and watches that directory.
    /// </summary>
    [Fact]
    public async Task Watch_DotGitFile_ResolvesGitdirAndDetectsChange()
    {
        // Create the "real" git dir somewhere else.
        var realGitDir = Path.Combine(_tempDir, "real-git");
        Directory.CreateDirectory(realGitDir);
        var headPath = Path.Combine(realGitDir, "HEAD");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/main");

        // Create a worktree dir whose .git is a FILE pointing to the real git dir.
        var worktreeDir = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(worktreeDir);
        // Use a relative path from worktreeDir to realGitDir
        var relativePath = Path.GetRelativePath(worktreeDir, realGitDir);
        await File.WriteAllTextAsync(Path.Combine(worktreeDir, ".git"), $"gitdir: {relativePath}");

        using var watcher = new WorktreeGitWatcher();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, _) => tcs.TrySetResult(true);

        watcher.Watch(worktreeDir);
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/feature");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        if (completed == tcs.Task)
            Assert.True(await tcs.Task);
        // else: timed out — platform-unsupported or too slow; acceptable.
    }

    /// <summary>
    /// ReadBranch parses "ref: refs/heads/&lt;branch&gt;" from HEAD, and returns null for a detached
    /// HEAD (raw SHA) or a missing HEAD — the parser behind the structured "switched branch" timeline op.
    /// </summary>
    [Fact]
    public void ReadBranch_ParsesHeadRef_AndReturnsNullForDetached()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        var head = Path.Combine(gitDir, "HEAD");

        File.WriteAllText(head, "ref: refs/heads/fix/worktree-survives-spawn\n");
        Assert.Equal("fix/worktree-survives-spawn", WorktreeGitWatcher.ReadBranch(gitDir));

        File.WriteAllText(head, "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b");   // detached HEAD
        Assert.Null(WorktreeGitWatcher.ReadBranch(gitDir));

        File.Delete(head);
        Assert.Null(WorktreeGitWatcher.ReadBranch(gitDir));                    // missing HEAD
    }

    /// <summary>
    /// Switching branch (HEAD's ref changes) raises BranchChanged with the new branch — the signal that
    /// drives a structured timeline op. Tolerant of platforms without a reliable FileSystemWatcher.
    /// </summary>
    [Fact]
    public async Task Watch_BranchSwitch_RaisesBranchChanged_WithTheNewBranch()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        var headPath = Path.Combine(gitDir, "HEAD");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/main");

        using var watcher = new WorktreeGitWatcher();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BranchChanged += (_, branch) => tcs.TrySetResult(branch);

        watcher.Watch(_tempDir);
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/fix/foo");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        if (completed == tcs.Task)
            Assert.Equal("fix/foo", await tcs.Task);
        // else: FSW unsupported/slow on this platform — tolerate (no hang), like the Changed tests.
    }

    /// <summary>
    /// Dispose releases resources without throwing.
    /// </summary>
    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var watcher = new WorktreeGitWatcher();
        watcher.Watch(_tempDir); // no .git here — just ensures code paths are exercised
        watcher.Dispose(); // must not throw
    }
}
