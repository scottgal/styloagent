namespace Styloagent.App.Browser;

/// <summary>
/// Resolves an approved opaque reference inside the broker. Implementations must never log or persist
/// returned values. The default provider refuses credentialed runs until a real secret store is wired.
/// </summary>
public interface IBrowserCredentialProvider
{
    Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct);
}

public sealed class RejectingBrowserCredentialProvider : IBrowserCredentialProvider
{
    public Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct)
        => throw new InvalidOperationException("credential provider is not configured");
}
