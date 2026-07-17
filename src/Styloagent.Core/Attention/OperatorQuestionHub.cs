namespace Styloagent.Core.Attention;

/// <summary>
/// Bridges the <see cref="OperatorQuestionStore"/> to answer delivery — the bus--owned "answer callback"
/// the cockpit's operator-question top bar drives.
///
/// The <c>ask_operator</c> verb <see cref="Post"/>s a structured question; the top bar reads
/// <see cref="Pending"/> / observes <see cref="Changed"/> and, when the operator clicks an option, calls
/// <see cref="AnswerAsync"/>. That formats the answer and delivers it back to the asking agent as a normal
/// bus message via the caller-supplied <c>deliverAnswer</c> delegate (the cockpit wires this to its own
/// <c>SendBusMessage</c> path — the same route every message takes, so MCP-native pull delivery applies),
/// then clears the pending question.
///
/// The delegate keeps this Core-side and dependency-free: <b>bus-</b> owns the answer semantics (sender,
/// subject/body, clear-on-delivery), while the cockpit owns only the transport primitive it already has.
/// </summary>
public sealed class OperatorQuestionHub
{
    /// <summary>The synthetic sender prefix an operator answer is delivered under (the cockpit's
    /// <c>deliverAnswer</c> maps this to the message's <c>From</c>).</summary>
    public const string OperatorPrefix = "operator-";

    private readonly OperatorQuestionStore _store;
    private readonly Func<string, string, string, Task> _deliverAnswer; // (toPrefix, subject, body)

    public OperatorQuestionHub(OperatorQuestionStore store, Func<string, string, string, Task> deliverAnswer)
        => (_store, _deliverAnswer) = (store, deliverAnswer);

    /// <summary>Every pending question, oldest-asked first — for the top bar.</summary>
    public IReadOnlyList<OperatorQuestion> Pending => _store.Pending;

    /// <summary>Raised whenever the pending set changes — the top bar re-reads <see cref="Pending"/>.</summary>
    public event EventHandler? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    /// <summary>Record a structured question raised by <paramref name="askingPrefix"/> and return it.</summary>
    public OperatorQuestion Post(string askingPrefix, string question, IReadOnlyList<string> options, DateTimeOffset askedAt)
    {
        var q = new OperatorQuestion(askingPrefix, question.Trim(), options, askedAt);
        _store.Post(q);
        return q;
    }

    /// <summary>
    /// Deliver the operator's <paramref name="chosenOption"/> back to <paramref name="askingPrefix"/> and
    /// clear the pending question. Returns false when there is nothing pending for that agent (a stale or
    /// duplicate click). The question is claimed atomically up front so a double-click can't deliver twice;
    /// if delivery throws, the question is restored so the operator can retry.
    /// </summary>
    public async Task<bool> AnswerAsync(string askingPrefix, string chosenOption)
    {
        var q = _store.Remove(askingPrefix);
        if (q is null) return false;
        try
        {
            var (subject, body) = OperatorAnswer.Format(q, chosenOption);
            await _deliverAnswer(askingPrefix, subject, body).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _store.Post(q);   // delivery failed — restore so the click can be retried
            throw;
        }
    }

    /// <summary>Drop a pending question without answering (operator dismissed it). False if none pending.</summary>
    public bool Dismiss(string askingPrefix) => _store.Remove(askingPrefix) is not null;
}
