using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;

namespace Styloagent.App.Views;

public partial class ChangesView : UserControl
{
    // Guard re-entrant updates when CurrentBranch changes drive the ComboBox SelectedItem.
    private bool _suppressBranchSwitch;

    public ChangesView() => InitializeComponent();

    /// <summary>
    /// Fires <see cref="ChangesViewModel.SelectFileAsync"/> when the user picks a file.
    /// Using code-behind keeps the AXAML clean and avoids over-engineering the binding.
    /// </summary>
    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ChangesViewModel vm &&
            ((SelectingItemsControl)sender!).SelectedItem is GitChange file)
        {
            _ = vm.SelectFileAsync(file);
        }
    }

    /// <summary>
    /// Fires <see cref="ChangesViewModel.SwitchAsync"/> when the user picks a branch in the
    /// ComboBox. Re-entrant guard prevents the VM's own reload from triggering a second switch.
    /// </summary>
    private void OnBranchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressBranchSwitch) return;
        if (DataContext is ChangesViewModel vm &&
            BranchComboBox.SelectedItem is GitBranch branch &&
            !branch.IsCurrent)
        {
            _suppressBranchSwitch = true;
            try { _ = vm.SwitchAsync(branch); }
            finally { _suppressBranchSwitch = false; }
        }
    }
}
