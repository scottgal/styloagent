using Styloagent.App.ViewModels;

namespace Styloagent.App.Tests;

/// <summary>
/// Compaction resilience (P3): the PreCompact fallback persists a best-effort resume anchor for an agent
/// right before a compaction — but NEVER clobbers a doc the agent authored itself (degrade-never-destroy),
/// and it's file-only (never frees the PTY). The 0.80 monitor + nudge + the InPlaceCheckpoint primitive
/// are unit-tested in Core; this covers the cockpit's fallback wiring. Live nudge delivery is
/// restart-verified.
/// </summary>
public class CompactionCheckpointTests
{
    [Fact]
    public async Task PreCompactCheckpoint_LogsOutcome_AndKeepsAnAgentAuthoredDoc()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var prefix = vm.Panes.First().Prefix;
            var docPath = Path.Combine(root, "saved-context", prefix + "context.md");
            var timelineBefore = vm.TimelineCount;

            // MakeTwoAgentChannel seeds a non-blank resume doc — the agent's own; the fallback must keep it.
            var original = File.Exists(docPath) ? File.ReadAllText(docPath) : null;

            await vm.WriteInPlaceCheckpointForTest(prefix);

            Assert.Equal(timelineBefore + 1, vm.TimelineCount);          // the checkpoint outcome is logged
            if (original is not null)
                Assert.Equal(original, File.ReadAllText(docPath));       // degrade-never-destroy: untouched
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
