namespace Styloagent.Core.Attention;

/// <summary>
/// A request from an agent (via the <c>open_document</c> MCP verb) to surface a document in the cockpit —
/// "here's THIS doc", so the operator is looking at the same thing. Distinct from an
/// <see cref="OperatorQuestion"/>: it is fire-and-forget (no answer routes back), the cockpit just opens the
/// file. <see cref="Path"/> is already canonicalized + scope-checked by <see cref="DocumentPathResolver"/>.
/// </summary>
/// <param name="AskingPrefix">The agent that asked to open it — the cockpit can show "&lt;prefix&gt;: &lt;reason&gt;".</param>
/// <param name="Path">The resolved absolute path to open (validated to be within an open repo root).</param>
/// <param name="Reason">Optional WHY it opened (pane title / header); null when the agent gave none.</param>
public sealed record DocumentOpenRequest(string AskingPrefix, string Path, string? Reason);
