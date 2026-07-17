namespace Styloagent.Core.Attention;

/// <summary>
/// The pending-operator-questions store: an in-memory, thread-safe projection of the structured questions
/// agents have raised to the human via <c>ask_operator</c>, each awaiting a one-click answer.
///
/// One pending question per asking agent — a re-ask replaces the prior. This is the bus--owned attention
/// item behind the cockpit's operator-question top bar: the <c>ask_operator</c> verb <see cref="Post"/>s
/// here; the top bar renders <see cref="Pending"/> and answers via <see cref="OperatorQuestionHub"/>, which
/// <see cref="Remove"/>s once the answer is delivered. <see cref="Changed"/> lets the top bar re-read on
/// every mutation.
///
/// Pure state — no delivery, no UI. Delivery lives in <see cref="OperatorQuestionHub"/> so this stays
/// trivially testable.
/// </summary>
public sealed class OperatorQuestionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OperatorQuestion> _pending = new();

    /// <summary>Raised after the pending set changes (a post, or a remove that actually removed something).</summary>
    public event EventHandler? Changed;

    /// <summary>Record (or replace) the pending question for its asking agent.</summary>
    public void Post(OperatorQuestion question)
    {
        lock (_gate) _pending[question.AskingPrefix] = question;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every pending question, oldest-asked first — the queue order the top bar renders.</summary>
    public IReadOnlyList<OperatorQuestion> Pending
    {
        get { lock (_gate) return _pending.Values.OrderBy(q => q.AskedAt).ToList(); }
    }

    /// <summary>The pending question for <paramref name="askingPrefix"/>, or null when none.</summary>
    public OperatorQuestion? Peek(string askingPrefix)
    {
        lock (_gate) return _pending.TryGetValue(askingPrefix, out var q) ? q : null;
    }

    /// <summary>
    /// Atomically remove and return the pending question for <paramref name="askingPrefix"/> (null if none).
    /// Raises <see cref="Changed"/> only when something was actually removed.
    /// </summary>
    public OperatorQuestion? Remove(string askingPrefix)
    {
        bool removed;
        OperatorQuestion? question;
        lock (_gate) removed = _pending.Remove(askingPrefix, out question);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return question;
    }
}
