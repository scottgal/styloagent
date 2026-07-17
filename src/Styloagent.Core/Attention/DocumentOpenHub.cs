namespace Styloagent.Core.Attention;

/// <summary>
/// Bridges the <c>open_document</c> verb to the cockpit: the verb <see cref="Post"/>s a resolved open-request
/// (path already canonicalized + scope-checked by <see cref="DocumentPathResolver"/>) and the hub raises
/// <see cref="Opened"/>, which the cockpit subscribes to — marshalling to the UI thread — to open the document.
///
/// Fire-and-forget: unlike <see cref="OperatorQuestionHub"/> there is no answer to route back and nothing to
/// persist, so there is no store. When nothing is subscribed (the hub is not wired), <see cref="Post"/> is a
/// harmless no-op — the same graceful-degrade posture as <c>ask_operator</c> with a null hub.
/// </summary>
public sealed class DocumentOpenHub
{
    /// <summary>Raised when an agent asks to open a document. The subscriber opens <c>Path</c> on the UI thread.</summary>
    public event EventHandler<DocumentOpenRequest>? Opened;

    /// <summary>Post a resolved open-request from <paramref name="askingPrefix"/> and return it. Blank
    /// <paramref name="reason"/> normalizes to null (no "why" to show).</summary>
    public DocumentOpenRequest Post(string askingPrefix, string path, string? reason)
    {
        var req = new DocumentOpenRequest(
            askingPrefix, path, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        Opened?.Invoke(this, req);
        return req;
    }
}
