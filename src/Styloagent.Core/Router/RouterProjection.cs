using System.Globalization;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Router;

/// <summary>
/// Reads the markdown router ledger under <c>routerRoot</c> into a <see cref="RouterState"/>.
/// Tolerant: missing dirs → empty; a malformed file is skipped. The only I/O component of the engine.
/// </summary>
public static partial class RouterProjection
{
    [GeneratedRegex(@"^\*\*From:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex FromRx();
    [GeneratedRegex(@"^\*\*Holder:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex HolderRx();
    [GeneratedRegex(@"^\*\*Timestamp:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex TsRx();
    [GeneratedRegex(@"^\*\*Purpose:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex PurposeRx();
    [GeneratedRegex(@"^\*\*Granted:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex GrantedRx();
    [GeneratedRegex(@"^\*\*ClaimTimestamp:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex ClaimTsRx();

    public static RouterState Read(string routerRoot)
    {
        var resources = new List<ResourceState>();
        try
        {
            if (!Directory.Exists(routerRoot)) return new RouterState(resources);
            foreach (var envDir in Directory.EnumerateDirectories(routerRoot))
            {
                var env = Path.GetFileName(envDir);
                ReadKind(env, ResourceKind.Account, Path.Combine(envDir, "accounts"), resources);
                ReadKind(env, ResourceKind.Slot, Path.Combine(envDir, "slots"), resources);
            }
        }
        catch { /* tolerant */ }
        return new RouterState(resources);
    }

    private static void ReadKind(string env, ResourceKind kind, string kindDir, List<ResourceState> into)
    {
        if (!Directory.Exists(kindDir)) return;
        foreach (var resDir in Directory.EnumerateDirectories(kindDir))
        {
            try
            {
                var name = Path.GetFileName(resDir);
                var policy = RouterPolicyReader.Read(Path.Combine(resDir, "resource.yaml"));
                var claims = ReadClaims(Path.Combine(resDir, "claims"));
                var grants = ReadGrants(Path.Combine(resDir, "grants"));
                var attempts = ReadAttempts(Path.Combine(resDir, "attempts.md"));
                into.Add(new ResourceState(env, kind, name, policy, claims, grants, attempts));
            }
            catch { /* skip malformed resource */ }
        }
    }

    private static List<Claim> ReadClaims(string dir)
    {
        var list = new List<Claim>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
        {
            try
            {
                var body = File.ReadAllText(f);
                var prefix = FromRx().Match(body) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
                var ts = ParseTs(TsRx().Match(body));
                if (prefix is null || ts is null) continue;
                var purpose = PurposeRx().Match(body) is { Success: true } p ? p.Groups[1].Value.Trim() : "";
                list.Add(new Claim(prefix, ts.Value, purpose));
            }
            catch { }
        }
        return list;
    }

    private static List<Grant> ReadGrants(string dir)
    {
        var list = new List<Grant>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
        {
            try
            {
                var body = File.ReadAllText(f);
                var prefix = HolderRx().Match(body) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
                if (prefix is null) continue;
                var granted = ParseTs(GrantedRx().Match(body)) ?? ToOffset(File.GetLastWriteTimeUtc(f));
                var claimTs = ParseTs(ClaimTsRx().Match(body)) ?? ToOffset(File.GetLastWriteTimeUtc(f));
                var heartbeat = new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero);
                list.Add(new Grant(prefix, granted, heartbeat, claimTs));
            }
            catch { }
        }
        return list;
    }

    private static List<AttemptLine> ReadAttempts(string file)
    {
        var list = new List<AttemptLine>();
        if (!File.Exists(file)) return list;
        try
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var sp = line.Split(' ', 2);
                if (sp.Length < 2) continue;
                if (!DateTimeOffset.TryParse(sp[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
                list.Add(new AttemptLine(ts, sp[1].Trim().Equals("ok", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }
        // Guarantee chronological order so consumers (IsCooling's last-success + fail window)
        // are correct regardless of the file's line order.
        return list.OrderBy(a => a.Timestamp).ToList();
    }

    private static DateTimeOffset? ParseTs(Match m)
        => m.Success && DateTimeOffset.TryParse(m.Groups[1].Value.Trim(), CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts) ? ts : null;

    // GetLastWriteTimeUtc returns a DateTime(Kind=Utc); wrap as DateTimeOffset for granted/claimTs fallbacks.
    private static DateTimeOffset ToOffset(DateTime utc) => new(utc, TimeSpan.Zero);
}
