using Avalonia.Controls;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

public partial class MarkdownDocumentView : UserControl
{
    public MarkdownDocumentView()
    {
        InitializeComponent();
        // Surface C4 element clicks to the document VM (→ the shell focuses the owning agent).
        Md.C4ElementClicked += id => (DataContext as MarkdownDocumentViewModel)?.RaiseComponentClicked(id);
    }
}
