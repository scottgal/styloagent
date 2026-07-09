using Avalonia.Controls;

namespace Styloagent.App.Views;

/// <summary>
/// The top-level Styloagent Dock window.
/// Hosts a 3-column shell: left bus placeholder | agent pane | right bus placeholder.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
