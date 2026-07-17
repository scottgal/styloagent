using Styloagent.App.ViewModels;

namespace Styloagent.App.Tests;

/// <summary>
/// P0 memory leak (operator-reported, gcdump-confirmed): switching layout modes rebuilds the whole dock
/// tree (BuildLayout → new RootDock) but never releases the OLD one, so every prior layout tree stays
/// rooted (via the reused pane VMs' Dock navigation adapters + factory tracking) — leaking the AgentPaneView
/// / TerminalControl subtrees and their scrollback + render resources. This model-level guard switches
/// layouts repeatedly and asserts the old RootDock trees are collectible.
/// </summary>
public class LayoutSwitchLeakTests
{
    [Fact]
    public async Task SwitchingLayoutModesRepeatedly_DoesNotRetainOldLayoutTrees()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            var weakOldLayouts = new List<WeakReference>();
            // Switch modes MANY times (each differs from the previous, so it always rebuilds). The leak was
            // UNBOUNDED — one whole dock tree retained per switch — so with N switches we'd retain ~N trees.
            var modes = new[] { "Tile", "Tabs", "AutoTile", "Tabs", "Tile", "AutoTile" };
            for (int i = 0; i < 18; i++)
            {
                if (vm.Layout is not null) weakOldLayouts.Add(new WeakReference(vm.Layout));
                vm.SetLayoutModeCommand.Execute(modes[i % modes.Length]);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // The fix must keep retention BOUNDED (only the current + at most the immediately-previous tree),
            // not growing with the number of switches. Pre-fix this was ~18; a small constant proves no
            // per-switch accumulation.
            var leaked = weakOldLayouts.Count(w => w.IsAlive);
            Assert.True(leaked <= 2,
                $"{leaked}/{weakOldLayouts.Count} old dock layout trees retained after GC — layout switch leaks (unbounded).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
