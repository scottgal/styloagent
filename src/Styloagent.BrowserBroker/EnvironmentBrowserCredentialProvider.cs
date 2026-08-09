using System.Collections.Concurrent;

namespace Styloagent.BrowserBroker;

/// <summary>
/// Centralized header / API-key resolution for governed browser runs. A credential reference is an
/// allow-listed, comma-separated list of <c>HeaderName=env:ENV_VAR</c> entries — e.g.
/// <c>X-Api-Key=env:STYLOBOT_API_KEY</c>. Secrets live in the host environment (or the machine's keychain,
/// surfaced as an env var), are resolved only at run time, are never logged or persisted, and never
/// reach artifacts. This is the single rule surface every client shares, so projects stop hard-coding
/// headers/keys in their own broker code.
/// </summary>
public sealed class EnvironmentBrowserCredentialProvider : IBrowserCredentialProvider
{
    /// <summary>How one <c>HeaderName=env:VAR</c> entry is written inside a credential reference.</summary>
    public const string EnvPrefix = "env:";

    public Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in credentialRef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var name = raw[..eq].Trim();
            var valueSpec = raw[(eq + 1)..].Trim();
            if (name.Length == 0 || !valueSpec.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = Environment.GetEnvironmentVariable(valueSpec[EnvPrefix.Length..]);
            if (value is not null) headers[name] = value;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(headers);
    }
}
