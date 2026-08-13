namespace Styloagent.Core.Environments;

/// <summary>What a credential reference entry's source side points at.</summary>
public enum CredentialSourceKind
{
    /// <summary><c>env:VAR</c> — the header value is process environment variable VAR.</summary>
    Env,
    /// <summary><c>keychain://ITEM</c> — the header value is the macOS generic password with service ITEM.</summary>
    Keychain,
    /// <summary><c>secret://NAME</c> — the header value is process environment variable NAME holding the literal value.</summary>
    Secret,
}

/// <summary>One parsed <c>HeaderName=source</c> entry inside a credential reference.</summary>
public readonly record struct CredentialRefEntry(string HeaderName, CredentialSourceKind Source, string Name);

/// <summary>
/// Grammar and validation for credential references. A credential reference is a comma-separated list
/// of <c>HeaderName=source</c> entries where source is <c>env:VAR</c>, <c>keychain://ITEM</c>, or
/// <c>secret://NAME</c>. The source side is always a spec pointing at where the value lives — a
/// reference can never carry literal secret material, which is the security invariant this grammar
/// exists to enforce. Validation is strict (any malformed entry invalidates the whole reference) while
/// resolution is lenient (a missing value at run time is skipped silently).
/// </summary>
public static class CredentialReference
{
    public const string EnvPrefix = "env:";
    public const string KeychainPrefix = "keychain://";
    public const string SecretPrefix = "secret://";

    /// <summary>Maximum length of a whole reference, bounds-checked before any parsing.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// True when the reference is unset or every comma-separated entry parses as
    /// <c>HeaderName=source</c>. Used by validators: a ref that passes this shape is guaranteed not to
    /// carry literal secret material, because the source side is grammar-restricted to specs.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > MaxLength) return false;
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseEntry(raw, out _)) return false;
        }
        return true;
    }

    /// <summary>
    /// Parses every well-formed <c>HeaderName=source</c> entry in the reference. Malformed entries are
    /// skipped, matching the existing <c>env:</c> tolerance — an entry that cannot be expressed is not
    /// a reason to fail the whole list. The empty list is returned for an empty/unset reference.
    /// </summary>
    public static IReadOnlyList<CredentialRefEntry> ParseEntries(string value)
    {
        var entries = new List<CredentialRefEntry>();
        if (string.IsNullOrWhiteSpace(value)) return entries;
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseEntry(raw, out var entry)) entries.Add(entry);
        }
        return entries;
    }

    private static bool TryParseEntry(string raw, out CredentialRefEntry entry)
    {
        entry = default;
        var eq = raw.IndexOf('=');
        if (eq <= 0) return false;
        var name = raw[..eq].Trim();
        if (!ValidHeaderName(name)) return false;
        var source = raw[(eq + 1)..].Trim();
        if (source.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var variable = source[EnvPrefix.Length..];
            if (variable.Length == 0 || variable.Contains('=')) return false;
            entry = new CredentialRefEntry(name, CredentialSourceKind.Env, variable);
            return true;
        }
        if (source.StartsWith(KeychainPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var item = source[KeychainPrefix.Length..];
            if (item.Length == 0) return false;
            entry = new CredentialRefEntry(name, CredentialSourceKind.Keychain, item);
            return true;
        }
        if (source.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var variable = source[SecretPrefix.Length..];
            if (variable.Length == 0 || variable.Contains('=')) return false;
            entry = new CredentialRefEntry(name, CredentialSourceKind.Secret, variable);
            return true;
        }
        return false;
    }

    /// <summary>HTTP token characters (RFC 9110 tchar) plus the delimiters we need inside a list.</summary>
    private static bool ValidHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if ("!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)) continue;
            return false;
        }
        return true;
    }
}
