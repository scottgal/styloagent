using System.Globalization;

namespace Styloagent.Core.Router;

/// <summary>Coordinator-only writes to the ledger (grant files + log). Tolerant: never throws.</summary>
public static class RouterWriter
{
    public static void WriteGrant(string root, string env, ResourceKind kind, string name, string prefix,
        DateTimeOffset granted, DateTimeOffset expires, DateTimeOffset claimTs)
    {
        try
        {
            var dir = RouterPaths.GrantsDir(root, env, kind, name);
            Directory.CreateDirectory(dir);
            var body =
                $"**Holder:** {prefix}\n" +
                $"**Granted:** {granted.ToString("o", CultureInfo.InvariantCulture)}\n" +
                $"**Expires:** {expires.ToString("o", CultureInfo.InvariantCulture)}\n" +
                $"**ClaimTimestamp:** {claimTs.ToString("o", CultureInfo.InvariantCulture)}\n";
            File.WriteAllText(RouterPaths.GrantFile(root, env, kind, name, prefix), body);
        }
        catch { }
    }

    public static void DeleteGrant(string root, string env, ResourceKind kind, string name, string prefix)
    {
        try
        {
            var f = RouterPaths.GrantFile(root, env, kind, name, prefix);
            if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }

    public static void AppendLog(string root, string env, ResourceKind kind, string name, string line)
    {
        try
        {
            var dir = RouterPaths.ResourceDir(root, env, kind, name);
            Directory.CreateDirectory(dir);
            File.AppendAllText(RouterPaths.LogFile(root, env, kind, name),
                $"{DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)} {line}\n");
        }
        catch { }
    }
}
