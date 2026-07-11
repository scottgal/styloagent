using System.Linq;

namespace Styloagent.Core.Router;

/// <summary>Ledger path helpers. Kind maps to the on-disk dir (accounts/slots).</summary>
public static class RouterPaths
{
    public static string KindDir(ResourceKind kind) => kind == ResourceKind.Slot ? "slots" : "accounts";
    public static string ResourceDir(string root, string env, ResourceKind kind, string name)
        => Path.Combine(root, env, KindDir(kind), name);
    public static string GrantsDir(string root, string env, ResourceKind kind, string name)
        => Path.Combine(ResourceDir(root, env, kind, name), "grants");
    public static string ClaimsDir(string root, string env, ResourceKind kind, string name)
        => Path.Combine(ResourceDir(root, env, kind, name), "claims");
    public static string GrantFile(string root, string env, ResourceKind kind, string name, string prefix)
        => Path.Combine(GrantsDir(root, env, kind, name), Sanitize(prefix) + ".md");
    public static string AttemptsFile(string root, string env, ResourceKind kind, string name)
        => Path.Combine(ResourceDir(root, env, kind, name), "attempts.md");
    public static string LogFile(string root, string env, ResourceKind kind, string name)
        => Path.Combine(ResourceDir(root, env, kind, name), "log.md");

    /// <summary>File-safe form of a prefix/name (keep alnum, '-', '_').</summary>
    public static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return chars.Length == 0 ? "x" : new string(chars);
    }
}
