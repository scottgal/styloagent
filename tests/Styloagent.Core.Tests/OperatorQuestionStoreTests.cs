using Styloagent.Core.Attention;
using Xunit;

namespace Styloagent.Core.Tests;

public class OperatorQuestionStoreTests
{
    private static readonly string[] YesNo = { "Yes", "No" };
    private static readonly string[] ExpectedOldestFirst = { "old-", "mid-", "young-" };

    private static OperatorQuestion Q(string prefix, string question, int askedMinutesAgo = 0)
        => new(prefix, question, YesNo, DateTimeOffset.UtcNow.AddMinutes(-askedMinutesAgo));

    [Fact]
    public void Post_then_Pending_returns_the_question()
    {
        var store = new OperatorQuestionStore();
        store.Post(Q("foss-", "Ship it?"));

        var pending = store.Pending;
        Assert.Single(pending);
        Assert.Equal("foss-", pending[0].AskingPrefix);
        Assert.Equal("Ship it?", pending[0].Question);
    }

    [Fact]
    public void Post_replaces_the_prior_question_for_the_same_agent()
    {
        var store = new OperatorQuestionStore();
        store.Post(Q("foss-", "First?"));
        store.Post(Q("foss-", "Second?"));

        Assert.Single(store.Pending);                    // one pending per agent
        Assert.Equal("Second?", store.Pending[0].Question);
    }

    [Fact]
    public void Pending_orders_oldest_asked_first()
    {
        var store = new OperatorQuestionStore();
        store.Post(Q("young-", "y", askedMinutesAgo: 1));
        store.Post(Q("old-", "o", askedMinutesAgo: 10));
        store.Post(Q("mid-", "m", askedMinutesAgo: 5));

        Assert.Equal(ExpectedOldestFirst, store.Pending.Select(q => q.AskingPrefix));
    }

    [Fact]
    public void Peek_returns_the_agents_question_or_null()
    {
        var store = new OperatorQuestionStore();
        store.Post(Q("foss-", "Ship it?"));

        Assert.Equal("Ship it?", store.Peek("foss-")!.Question);
        Assert.Null(store.Peek("router-"));
    }

    [Fact]
    public void Remove_returns_and_clears_the_question()
    {
        var store = new OperatorQuestionStore();
        store.Post(Q("foss-", "Ship it?"));

        var removed = store.Remove("foss-");
        Assert.Equal("Ship it?", removed!.Question);
        Assert.Empty(store.Pending);
        Assert.Null(store.Remove("foss-"));              // second remove is a no-op
    }

    [Fact]
    public void Changed_fires_on_post_and_on_remove()
    {
        var store = new OperatorQuestionStore();
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Post(Q("foss-", "Ship it?"));
        store.Remove("foss-");
        store.Remove("foss-");                            // no-op → no event

        Assert.Equal(2, changes);
    }
}
