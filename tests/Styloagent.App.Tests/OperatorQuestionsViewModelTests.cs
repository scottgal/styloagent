using Styloagent.App.ViewModels;
using Styloagent.Core.Attention;

namespace Styloagent.App.Tests;

public class OperatorQuestionsViewModelTests
{
    private static readonly string[] YesNo = { "yes", "no" };
    private static readonly string[] One = { "1" };
    private static readonly string[] Two = { "2" };

    private static (OperatorQuestionHub hub, List<(string to, string subject, string body)> delivered) MakeHub()
    {
        var delivered = new List<(string, string, string)>();
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(),
            (to, subject, body) => { delivered.Add((to, subject, body)); return Task.CompletedTask; });
        return (hub, delivered);
    }

    [Fact]
    public void Posting_a_question_populates_the_banner()
    {
        var (hub, _) = MakeHub();
        var vm = new OperatorQuestionsViewModel(hub);
        Assert.False(vm.HasQuestions);

        hub.Post("foss-", "Ship it?", YesNo, DateTimeOffset.UtcNow);

        Assert.True(vm.HasQuestions);
        var item = Assert.Single(vm.Questions);
        Assert.Equal("foss-", item.AskingPrefix);
        Assert.Equal("Ship it?", item.Question);
        Assert.Equal(YesNo, item.Options);
        Assert.Equal("Ship it?", vm.PendingByPrefix["foss-"]);
    }

    [Fact]
    public async Task Answering_an_option_delivers_the_choice_and_clears_the_question()
    {
        var (hub, delivered) = MakeHub();
        var vm = new OperatorQuestionsViewModel(hub);
        hub.Post("foss-", "Ship it?", YesNo, DateTimeOffset.UtcNow);
        var item = Assert.Single(vm.Questions);

        await item.AnswerCommand.ExecuteAsync("yes");

        var d = Assert.Single(delivered);
        Assert.Equal("foss-", d.to);            // routed back to the asker
        Assert.Contains("yes", d.body);          // the chosen option reaches it
        Assert.False(vm.HasQuestions);           // cleared from the banner
        Assert.Empty(vm.Questions);
    }

    [Fact]
    public void Two_questions_queue_oldest_first_and_map_by_prefix()
    {
        var (hub, _) = MakeHub();
        var vm = new OperatorQuestionsViewModel(hub);

        hub.Post("foss-", "A?", One, new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero));
        hub.Post("docs-", "B?", Two, new DateTimeOffset(2026, 7, 17, 10, 1, 0, TimeSpan.Zero));

        Assert.Equal(2, vm.Questions.Count);
        Assert.Equal("foss-", vm.Questions[0].AskingPrefix);   // oldest first
        Assert.Equal("docs-", vm.Questions[1].AskingPrefix);
        Assert.Equal("A?", vm.PendingByPrefix["foss-"]);
        Assert.Equal("B?", vm.PendingByPrefix["docs-"]);
    }

    [Fact]
    public void Dispose_unsubscribes_and_does_not_throw()
    {
        var (hub, _) = MakeHub();
        var vm = new OperatorQuestionsViewModel(hub);
        vm.Dispose();
        var ex = Record.Exception(() => hub.Post("foss-", "late", One, DateTimeOffset.UtcNow));
        Assert.Null(ex);
        Assert.Empty(vm.Questions);   // no longer reconciling after dispose
    }
}
