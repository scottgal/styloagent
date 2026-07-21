using Avalonia.Controls;

namespace Styloagent.App.Views;

public partial class RenameDialog : Window
{
    public RenameDialog(string currentName) : this()
    {
        NameBox.Text = currentName;
        NameBox.SelectAll();
        RenameButton.Click += (_, _) => Close(string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim());
        CancelButton.Click += (_, _) => Close(null);
    }

    public RenameDialog() => InitializeComponent();
}
