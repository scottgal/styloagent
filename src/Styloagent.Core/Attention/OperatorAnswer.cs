namespace Styloagent.Core.Attention;

/// <summary>
/// Formats the bus message that carries an operator's answer back to the agent that asked. Pure so the
/// exact wording is unit-testable and consistent with what the asking agent reads in its inbox.
/// </summary>
public static class OperatorAnswer
{
    /// <summary>
    /// The (subject, body) for the answer message: the chosen option plus the original question for
    /// context, so the agent has a self-contained record without re-reading its own transcript.
    /// </summary>
    public static (string Subject, string Body) Format(OperatorQuestion question, string chosenOption)
    {
        var choice = (chosenOption ?? string.Empty).Trim();
        var subject = $"Operator answered: {choice}";
        var body =
            "You asked the operator:\n\n" +
            $"> {question.Question}\n\n" +
            $"The operator chose: **{choice}**";
        return (subject, body);
    }
}
