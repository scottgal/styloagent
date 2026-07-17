using Avalonia.Controls;

namespace Styloagent.App.Views;

/// <summary>
/// The fleet-wide operator-question banner (ask_operator). Renders an <c>OperatorQuestionsViewModel</c>'s
/// pending questions with one-click answer buttons; self-hides when nothing is pending. Hosted at the top
/// of the cockpit shell.
/// </summary>
public partial class OperatorQuestionBannerView : UserControl
{
    public OperatorQuestionBannerView() => InitializeComponent();
}
