namespace Styloagent.Core.Attention;

/// <summary>
/// A structured question an agent has raised to the HUMAN operator (via the <c>ask_operator</c> MCP verb),
/// awaiting a one-click answer. Distinct from an agent-to-agent bus message: it is surfaced to the operator
/// in the cockpit's question top bar, and the chosen option is delivered back to <see cref="AskingPrefix"/>
/// as a normal bus message.
/// </summary>
/// <param name="AskingPrefix">The prefix of the agent that raised the question (where the answer routes back).</param>
/// <param name="Question">The question text shown to the operator.</param>
/// <param name="Options">The answer choices the operator picks between (rendered as one-click buttons).</param>
/// <param name="AskedAt">When it was raised — drives oldest-first queue order in the top bar.</param>
public sealed record OperatorQuestion(
    string AskingPrefix,
    string Question,
    IReadOnlyList<string> Options,
    DateTimeOffset AskedAt);
