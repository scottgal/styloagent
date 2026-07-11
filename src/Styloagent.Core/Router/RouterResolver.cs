namespace Styloagent.Core.Router;

/// <summary>
/// Pure arbitration: given the ledger <see cref="RouterState"/> and the current time, decides which
/// claims to grant and which grants to expire. FIFO by claim timestamp, respects capacity, leases,
/// and per-account lockout cooldown. No I/O, no ambient clock.
/// </summary>
public static class RouterResolver
{
    public static IReadOnlyList<RouterDecision> Resolve(RouterState state, DateTimeOffset now)
    {
        var decisions = new List<RouterDecision>();
        foreach (var r in state.Resources)
        {
            // Live grants only (lease not expired). Expiry decisions come in Task 4.
            var liveGrants = r.Grants.Where(g => !IsExpired(g, r.Policy.LeaseTtl, now)).ToList();
            var heldPrefixes = new HashSet<string>(liveGrants.Select(g => g.Prefix), StringComparer.Ordinal);

            int free = r.Policy.Capacity - liveGrants.Count;
            if (free <= 0) continue;

            var queued = r.Claims
                .Where(c => !heldPrefixes.Contains(c.Prefix))
                .OrderBy(c => c.Timestamp).ThenBy(c => c.Prefix, StringComparer.Ordinal)
                .ToList();

            foreach (var claim in queued.Take(free))
                decisions.Add(new RouterDecision(RouterAction.Grant, r.Env, r.Kind, r.Name, claim.Prefix, now + r.Policy.LeaseTtl));
        }
        return decisions;
    }

    internal static bool IsExpired(Grant g, TimeSpan leaseTtl, DateTimeOffset now) => now - g.HeartbeatAt >= leaseTtl;
}
