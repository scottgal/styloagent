using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;

namespace Styloagent.App.Views;

public partial class ChangesView : UserControl
{
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
}
