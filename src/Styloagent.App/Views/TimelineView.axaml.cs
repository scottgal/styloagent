using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Styloagent.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
