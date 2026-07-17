using Styloagent.Core.Attention;
using Xunit;

namespace Styloagent.Core.Tests;

public class OperatorQuestionHubTests
{
    private sealed record Delivered(string To, string Subject, string Body);

    private static readonly string[] YesNo = { "Yes", "No" };

    [Fact]
    public async Task Post_records_the_question_and_it_appears_in_pending()
    {
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(), (_, _, _) => Task.CompletedTask);

        hub.Post("foss-", "  Ship it?  ", YesNo, DateTimeOffset.UtcNow);

        Assert.Single(hub.Pending);
        Assert.Equal("Ship it?", hub.Pending[0].Question);       // trimmed
        Assert.Equal(YesNo, hub.Pending[0].Options);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnswerAsync_delivers_the_chosen_option_to_the_asking_agent_and_clears_it()
    {
        Delivered? sent = null;
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(),
            (to, subject, body) => { sent = new Delivered(to, subject, body); return Task.CompletedTask; });
        hub.Post("foss-", "Ship it?", YesNo, DateTimeOffset.UtcNow);

        var ok = await hub.AnswerAsync("foss-", "Yes");

        Assert.True(ok);
        Assert.Equal("foss-", sent!.To);                         // routed back to the asker
        Assert.Contains("Yes", sent.Subject + sent.Body);        // the chosen option is carried
        Assert.Contains("Ship it?", sent.Body);                  // with the original question for context
        Assert.Empty(hub.Pending);                               // cleared on delivery
    }

    [Fact]
    public async Task AnswerAsync_returns_false_when_there_is_no_pending_question()
    {
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(), (_, _, _) => Task.CompletedTask);

        Assert.False(await hub.AnswerAsync("foss-", "Yes"));      // stale/duplicate click
    }

    [Fact]
    public async Task AnswerAsync_restores_the_question_when_delivery_throws()
    {
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(),
            (_, _, _) => throw new InvalidOperationException("channel down"));
        hub.Post("foss-", "Ship it?", YesNo, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.AnswerAsync("foss-", "Yes"));

        Assert.Single(hub.Pending);                              // still answerable after a failed delivery
    }

    [Fact]
    public async Task Dismiss_clears_without_delivering()
    {
        bool delivered = false;
        var hub = new OperatorQuestionHub(new OperatorQuestionStore(),
            (_, _, _) => { delivered = true; return Task.CompletedTask; });
        hub.Post("foss-", "Ship it?", YesNo, DateTimeOffset.UtcNow);

        Assert.True(hub.Dismiss("foss-"));
        Assert.Empty(hub.Pending);
        Assert.False(delivered);
        Assert.False(hub.Dismiss("foss-"));                      // nothing left to dismiss
    }

    [Fact]
    public void OperatorPrefix_is_the_synthetic_answer_sender()
        => Assert.Equal("operator-", OperatorQuestionHub.OperatorPrefix);
}
