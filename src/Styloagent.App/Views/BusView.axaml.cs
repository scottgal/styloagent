using Avalonia.Controls;
using Avalonia.Input;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

public partial class BusView : UserControl
{
    public BusView()
    {
        InitializeComponent();
    }

    /// <summary>Double-clicking a message row opens its full markdown as a document.</summary>
    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BusMessageItem item } && DataContext is BusViewModel vm)
            vm.OpenMessageCommand.Execute(item);
    }
}
