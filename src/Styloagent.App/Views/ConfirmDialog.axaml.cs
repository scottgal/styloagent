using Avalonia.Controls;

namespace Styloagent.App.Views;

/// <summary>A minimal modal yes/no confirm. <c>ShowDialog&lt;bool&gt;(owner)</c> returns true on Confirm,
/// false on Cancel (or close). Used for the top-bar graceful shut down.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message, string confirmLabel = "Shut down") : this()
    {
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
