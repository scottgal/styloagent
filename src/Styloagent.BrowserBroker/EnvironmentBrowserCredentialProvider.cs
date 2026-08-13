using System.Diagnostics;
using Styloagent.Core.Environments;

namespace Styloagent.BrowserBroker;

/// <summary>
/// Centralized header / API-key resolution for governed browser runs. A credential reference is an
/// allow-listed, comma-separated list of <c>HeaderName=source</c> entries, e.g.
/// <c>X-SB-Api-Key=keychain://staging-debug-key, X-Trace=env:TRACE_ID</c>. Source is one of:
/// <list type="bullet">
/// <item><c>env:VAR</c> — the header value is process environment variable VAR.</item>
/// <item><c>keychain://ITEM</c> — the header value is the macOS generic password with service ITEM,
/// read at run time via <c>security find-generic-password -s "ITEM" -w</c>. The value is never logged,
/// persisted, or written to job files, manifests, or artifacts.</item>
/// <item><c>secret://NAME</c> — the header value is process environment variable NAME holding the
/// literal value directly (a straight value, not another header spec).</item>
/// </list>
/// Entries whose source is invalid or missing at run time are skipped silently. If the reference
/// contained entries but none of them resolved, <see cref="ResolveHeadersAsync"/> throws so the caller
/// fails the run — a credentialed run must never proceed keyless.
/// </summary>
public sealed class EnvironmentBrowserCredentialProvider : IBrowserCredentialProvider
{
    /// <summary>How one <c>HeaderName=env:VAR</c> entry is written inside a credential reference.</summary>
    public const string EnvPrefix = CredentialReference.EnvPrefix;

    private readonly Func<string, string?> _keychainReader;

    /// <summary>Uses the real macOS keychain via the <c>security</c> CLI.</summary>
    public EnvironmentBrowserCredentialProvider() : this(null) { }

    /// <summary>Injects the keychain fetch (tests pass a stub; the default reads the macOS keychain).</summary>
    public EnvironmentBrowserCredentialProvider(Func<string, string?>? keychainReader)
        => _keychainReader = keychainReader ?? ReadFromKeychain;

    public Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(string credentialRef, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hadEntries = false;
        foreach (var entry in CredentialReference.ParseEntries(credentialRef))
        {
            hadEntries = true;
            var value = Resolve(entry.Source, entry.Name);
            // A keychain item that exists but holds an empty password is effectively missing — an
            // empty header would let the run proceed without an effective credential.
            var resolved = entry.Source == CredentialSourceKind.Keychain
                ? !string.IsNullOrEmpty(value)
                : value is not null;
            if (resolved) headers[entry.HeaderName] = value!;
        }
        if (hadEntries && headers.Count == 0)
            throw new InvalidOperationException("approved credential reference could not be resolved");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(headers);
    }

    public string? ResolveValue(string sourceSpec)
    {
        if (!CredentialReference.TryParseSource(sourceSpec, out var kind, out var name)) return null;
        var value = Resolve(kind, name);
        // Same fail-closed semantics as headers: an empty keychain value is not a resolved value.
        return kind == CredentialSourceKind.Keychain && string.IsNullOrEmpty(value) ? null : value;
    }

    private string? Resolve(CredentialSourceKind kind, string name) => kind switch
    {
        CredentialSourceKind.Env => Environment.GetEnvironmentVariable(name),
        CredentialSourceKind.Secret => Environment.GetEnvironmentVariable(name),
        CredentialSourceKind.Keychain => _keychainReader(name),
        _ => null,
    };

    /// <summary>
    /// Reads a generic password from the macOS keychain. Never logs or persists the fetched value;
    /// a missing item, locked keychain, or missing CLI resolves to null (treated as an unresolvable
    /// source). The item name is passed as a single argv element — never shell-interpolated.
    /// </summary>
    private static string? ReadFromKeychain(string item)
    {
        var start = new ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("find-generic-password");
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(item);
        start.ArgumentList.Add("-w");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
        }
        catch { return null; }
    }
}
