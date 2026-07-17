using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Attention;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class OperatorQuestionBannerViewTests
{
    private static readonly string[] YesNo = { "yes", "no" };
    private readonly HeadlessAvaloniaFixture _fx;
    public OperatorQuestionBannerViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // Renders the real banner over an OperatorQuestionsViewModel and proves (1) the question + asker render,
    // (2) the one-click option buttons materialize with their RelativeSource-bound AnswerCommand resolved,
    // and (3) clicking an option delivers the choice back to the asker and clears the banner.
    [Fact]
    public Task Banner_renders_a_question_and_answering_delivers_then_clears()
    {
        return _fx.DispatchAsync(async () =>
        {
            var delivered = new List<(string to, string subject, string body)>();
            var hub = new OperatorQuestionHub(new OperatorQuestionStore(),
                (to, subject, body) => { delivered.Add((to, subject, body)); return Task.CompletedTask; });
            var vm = new OperatorQuestionsViewModel(hub);
            hub.Post("foss-", "Deploy to prod?", YesNo, DateTimeOffset.UtcNow);

            var view = new OperatorQuestionBannerView { DataContext = vm };
            var window = new Window { Width = 600, Height = 80, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(texts, s => s.Contains("Deploy to prod?"));
            Assert.Contains(texts, s => s.Contains("foss-"));

            // The option buttons render; their RelativeSource-bound command resolves to the item's AnswerCommand.
            var yes = window.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "yes");
            Assert.NotNull(yes.Command);

            yes.Command!.Execute(yes.CommandParameter);   // simulate the click
            await HeadlessRender.SettleAsync(window);

            var d = Assert.Single(delivered);
            Assert.Equal("foss-", d.to);          // routed back to the asker
            Assert.Contains("yes", d.body);        // the chosen option reached it
            Assert.False(vm.HasQuestions);         // answered → banner clears (self-hides)

            window.Close();
        });
    }
}
