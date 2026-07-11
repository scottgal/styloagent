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
            foreach (var g in r.Grants.Where(g => IsExpired(g, r.Policy.LeaseTtl, now)))
                decisions.Add(new RouterDecision(RouterAction.Expire, r.Env, r.Kind, r.Name, g.Prefix, null));

            var liveGrants = r.Grants.Where(g => !IsExpired(g, r.Policy.LeaseTtl, now)).ToList();
            var heldPrefixes = new HashSet<string>(liveGrants.Select(g => g.Prefix), StringComparer.Ordinal);

            int free = r.Policy.Capacity - liveGrants.Count;
            if (free <= 0) continue;

            if (IsCooling(r, now, out _)) continue;   // withhold new grants; existing grants untouched

            var queued = r.Claims
                .Where(c => !heldPrefixes.Contains(c.Prefix))
                .OrderBy(c => c.Timestamp).ThenBy(c => c.Prefix, StringComparer.Ordinal)
                .ToList();

            foreach (var claim in queued.Take(free))
                decisions.Add(new RouterDecision(RouterAction.Grant, r.Env, r.Kind, r.Name, claim.Prefix, now + r.Policy.LeaseTtl));
        }
        return decisions;
    }

    /// <summary>
    /// True when the account is in lockout cooldown: at least Budget failures since its last success,
    /// within Window, and now is before (the budget-th such failure + Cooldown). Slots / no-lockout resources never cool.
    /// </summary>
    public static bool IsCooling(ResourceState r, DateTimeOffset now, out DateTimeOffset until)
    {
        until = default;
        if (r.Policy.Lockout is not { } lo) return false;

        // Failures since the last success, within the window, newest-relevant-first.
        var lastOk = r.Attempts.Where(a => a.Ok).Select(a => (DateTimeOffset?)a.Timestamp).LastOrDefault();
        var fails = r.Attempts
            .Where(a => !a.Ok && a.Timestamp >= now - lo.Window && (lastOk is null || a.Timestamp > lastOk))
            .OrderBy(a => a.Timestamp)
            .ToList();
        if (fails.Count < lo.Budget) return false;

        var tripping = fails[lo.Budget - 1].Timestamp;   // the budget-th failure
        until = tripping + lo.Cooldown;
        return now < until;
    }

    internal static bool IsExpired(Grant g, TimeSpan leaseTtl, DateTimeOffset now) => now - g.HeartbeatAt >= leaseTtl;
}
