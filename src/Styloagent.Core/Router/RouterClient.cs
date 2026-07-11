using System.Globalization;

namespace Styloagent.Core.Router;

/// <summary>Agent-side ledger ops: drop a claim, heartbeat, release, log an attempt. Tolerant.</summary>
public static class RouterClient
{
    public static ResourceKind DetectKind(string root, string env, string name)
        => Directory.Exists(RouterPaths.ResourceDir(root, env, ResourceKind.Slot, name)) ? ResourceKind.Slot : ResourceKind.Account;

    public static string DropClaim(string root, string env, string name, string prefix, string purpose, DateTimeOffset ts)
    {
        var kind = DetectKind(root, env, name);
        var dir = RouterPaths.ClaimsDir(root, env, kind, name);
        Directory.CreateDirectory(dir);
        var stamp = ts.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var file = Path.Combine(dir, $"{stamp}-{RouterPaths.Sanitize(prefix)}.md");
        File.WriteAllText(file,
            $"**From:** {prefix}\n**Timestamp:** {ts.ToString("o", CultureInfo.InvariantCulture)}\n**Purpose:** {purpose}\n");
        return file;
    }

    public static bool Heartbeat(string root, string env, string name, string prefix)
    {
        try
        {
            var kind = DetectKind(root, env, name);
            var f = RouterPaths.GrantFile(root, env, kind, name, prefix);
            if (!File.Exists(f)) return false;
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow);
            return true;
        }
        catch { return false; }
    }

    public static void Release(string root, string env, string name, string prefix)
    {
        try
        {
            var kind = DetectKind(root, env, name);
            RouterWriter.DeleteGrant(root, env, kind, name, prefix);
            var claims = RouterPaths.ClaimsDir(root, env, kind, name);
            if (Directory.Exists(claims))
                foreach (var c in Directory.EnumerateFiles(claims, $"*-{RouterPaths.Sanitize(prefix)}.md"))
                    File.Delete(c);
        }
        catch { }
    }

    public static void LogAttempt(string root, string env, string account, bool ok, DateTimeOffset ts)
    {
        try
        {
            var dir = RouterPaths.ResourceDir(root, env, ResourceKind.Account, account);
            Directory.CreateDirectory(dir);
            File.AppendAllText(RouterPaths.AttemptsFile(root, env, ResourceKind.Account, account),
                $"{ts.ToString("o", CultureInfo.InvariantCulture)} {(ok ? "ok" : "fail")}\n");
        }
        catch { }
    }
}
