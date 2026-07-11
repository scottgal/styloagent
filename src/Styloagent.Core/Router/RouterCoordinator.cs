namespace Styloagent.Core.Router;

/// <summary>
/// Applies the resolver's decisions to the ledger: writes grant files for Grant decisions, deletes them
/// for Expire, and appends a log line per decision. The single writer of grants. Tolerant; returns the
/// decisions it applied so the host can notify on transitions. <paramref name="now"/> is injected.
/// </summary>
public static class RouterCoordinator
{
    public static IReadOnlyList<RouterDecision> Tick(string routerRoot, DateTimeOffset now)
    {
        var applied = new List<RouterDecision>();
        try
        {
            var state = RouterProjection.Read(routerRoot);
            var decisions = RouterResolver.Resolve(state, now);
            foreach (var d in decisions)
            {
                if (d.Action == RouterAction.Grant)
                {
                    var claimTs = FindClaimTimestamp(state, d) ?? now;
                    RouterWriter.WriteGrant(routerRoot, d.Env, d.Kind, d.Name, d.Prefix, now, d.Expires ?? now, claimTs);
                    RouterWriter.AppendLog(routerRoot, d.Env, d.Kind, d.Name, $"granted {d.Prefix}");
                }
                else // Expire
                {
                    RouterWriter.DeleteGrant(routerRoot, d.Env, d.Kind, d.Name, d.Prefix);
                    RouterWriter.AppendLog(routerRoot, d.Env, d.Kind, d.Name, $"expired {d.Prefix}");
                }
                applied.Add(d);
            }
        }
        catch { }
        return applied;
    }

    private static DateTimeOffset? FindClaimTimestamp(RouterState state, RouterDecision d)
        => state.Resources
            .FirstOrDefault(r => r.Env == d.Env && r.Kind == d.Kind && r.Name == d.Name)
            ?.Claims.FirstOrDefault(c => c.Prefix == d.Prefix)?.Timestamp;
}
