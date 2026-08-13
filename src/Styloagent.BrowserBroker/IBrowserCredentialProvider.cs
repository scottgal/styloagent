namespace Styloagent.BrowserBroker;

/// <summary>
/// Resolves approved references inside the broker. Implementations must never log or persist
/// returned values. The default provider refuses credentialed runs until a real secret store is wired.
/// </summary>
public interface IBrowserCredentialProvider
{
    /// <summary>Resolves a comma-separated list of <c>HeaderName=source</c> entries into headers.</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct);

    /// <summary>
    /// Resolves a bare source spec (<c>env:VAR</c> / <c>keychain://ITEM</c> / <c>secret://NAME</c>) to a
    /// single value — used for login email/password refs. Returns null when the source is missing.
    /// </summary>
    string? ResolveValue(string sourceSpec);
}

public sealed class RejectingBrowserCredentialProvider : IBrowserCredentialProvider
{
    public Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct)
        => throw new InvalidOperationException("credential provider is not configured");

    public string? ResolveValue(string sourceSpec)
        => throw new InvalidOperationException("credential provider is not configured");
}
