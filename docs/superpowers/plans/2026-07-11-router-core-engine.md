# SSH/Shell Router — Core Arbitration Engine (Plan A of B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the deterministic, UI-free arbitration engine for the router — the model, resource policy, the pure `RouterResolver` (FIFO grants up to capacity, lease-expiry reclaim, lockout cooldown), and the `RouterProjection` that reads the markdown ledger — all in `Styloagent.Core`, exhaustively unit-tested.

**Architecture:** Mirrors the message bus's pure core (`ChannelProjection`/`FleetGovernor`/`MessageDelivery`). Immutable records describe ledger state (`RouterState`); a pure `RouterResolver.Resolve(state, now)` returns grant/expire decisions with no I/O and no ambient clock; `RouterProjection` parses the on-disk ledger into `RouterState`. Plan B adds the I/O coordinator, MCP tools, and panel that consume this engine.

**Tech Stack:** .NET 10, C#, xUnit, VYaml (config).

## Global Constraints

- Target framework `net10.0`; `<Nullable>enable</Nullable>`; analyzers run **as errors** (resolve idiomatically; these are plain records/pure logic — expect none).
- All engine code is **pure and UI-free** in `src/Styloagent.Core/Router/`. `Styloagent.Core` must NOT gain new dependencies beyond its existing ones (it already references VYaml).
- **No ambient clock:** every time-dependent function takes `DateTimeOffset now` as a parameter (the resolver must be a pure function of `(state, now)`). Never call `DateTimeOffset.Now`/`UtcNow` inside `Styloagent.Core`.
- **No I/O in the resolver.** `RouterProjection` is the only engine component that touches the filesystem, and it never throws (a missing dir → empty state; a malformed file → skipped), mirroring `src/Styloagent.Core/Channel/ChannelProjection.cs`.
- All ledger timestamps are ISO-8601 UTC.
- **Cooldown is derived, not stored:** the resolver computes cooldown purely from the attempt log + `now` (no marker file). An account is cooling when it has `≥ budget` failures since its last success within `window`; cooldown lasts until `(timestamp of the budget-th such failure) + cooldown`.
- Commit directly to `main` (no new branch) per project convention. Author every commit:
  `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` ending with
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Create (all under `src/Styloagent.Core/Router/`):**
- `RouterModel.cs` — `ResourceKind` enum; `Claim`, `Grant`, `AttemptLine`, `ResourceState`, `RouterState` records.
- `RouterPolicy.cs` — `LockoutPolicy`, `ResourcePolicy` records + `RouterPolicyReader` (VYaml) + duration parse.
- `RouterDecision.cs` — `RouterAction` enum + `RouterDecision` record.
- `RouterResolver.cs` — the pure `Resolve` + `IsCooling` helper.
- `RouterProjection.cs` — reads the ledger dir tree into `RouterState`.

**Tests (under `tests/Styloagent.Core.Tests/`):**
- `RouterPolicyReaderTests.cs`, `RouterResolverTests.cs`, `RouterProjectionTests.cs`.

---

## Task 1: Router model + decision types

**Files:**
- Create: `src/Styloagent.Core/Router/RouterModel.cs`, `src/Styloagent.Core/Router/RouterDecision.cs`
- Test: `tests/Styloagent.Core.Tests/RouterResolverTests.cs` (a construction smoke test; the resolver arrives in Task 3)

**Interfaces:**
- Produces:
  - `enum ResourceKind { Account, Slot }`
  - `record Claim(string Prefix, DateTimeOffset Timestamp, string Purpose)`
  - `record Grant(string Prefix, DateTimeOffset GrantedAt, DateTimeOffset HeartbeatAt, DateTimeOffset ClaimTimestamp)`
  - `record AttemptLine(DateTimeOffset Timestamp, bool Ok)`
  - `record ResourceState(string Env, ResourceKind Kind, string Name, ResourcePolicy Policy, IReadOnlyList<Claim> Claims, IReadOnlyList<Grant> Grants, IReadOnlyList<AttemptLine> Attempts)`
  - `record RouterState(IReadOnlyList<ResourceState> Resources)`
  - `enum RouterAction { Grant, Expire }`
  - `record RouterDecision(RouterAction Action, string Env, ResourceKind Kind, string Name, string Prefix, DateTimeOffset? Expires)`

  (These reference `ResourcePolicy` from Task 2. To let Task 1 compile independently, define a minimal `ResourcePolicy` stub is NOT needed — do Task 2's `RouterPolicy.cs` first if the compiler complains, OR create both files in this task. SIMPLEST: create `RouterPolicy.cs` (Task 2's types) in Task 2, and in Task 1 create `RouterModel.cs`/`RouterDecision.cs` which reference `ResourcePolicy`. Because Task 2 runs next and the project won't build until both exist, **do Task 1 and Task 2 back-to-back**; the Task 1 smoke test is added but only runs green after Task 2. To keep Task 1 independently testable, its test only constructs the decision/enum types that do NOT depend on `ResourcePolicy`.)

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/RouterResolverTests.cs`:

```csharp
using Styloagent.Core.Router;
using Xunit;

public class RouterResolverTests
{
    [Fact]
    public void RouterDecision_carries_a_grant()
    {
        var d = new RouterDecision(RouterAction.Grant, "prod", ResourceKind.Account, "deploy", "foss-",
            new System.DateTimeOffset(2026, 7, 11, 12, 0, 0, System.TimeSpan.Zero));
        Assert.Equal(RouterAction.Grant, d.Action);
        Assert.Equal("deploy", d.Name);
        Assert.Equal("foss-", d.Prefix);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterResolverTests"`
Expected: FAIL — `Styloagent.Core.Router` types do not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Router/RouterDecision.cs`:

```csharp
namespace Styloagent.Core.Router;

/// <summary>A mutation the coordinator should apply to the ledger.</summary>
public enum RouterAction { Grant, Expire }

/// <summary>One resolver decision: grant a claim (with a lease <see cref="Expires"/>) or expire a grant.</summary>
public sealed record RouterDecision(
    RouterAction Action, string Env, ResourceKind Kind, string Name, string Prefix, DateTimeOffset? Expires);
```

Create `src/Styloagent.Core/Router/RouterModel.cs`:

```csharp
namespace Styloagent.Core.Router;

/// <summary>An SSH account (capacity 1, lockout-tracked) or a test slot pool (capacity N).</summary>
public enum ResourceKind { Account, Slot }

/// <summary>A pending claim request (drop-once markdown file).</summary>
public sealed record Claim(string Prefix, DateTimeOffset Timestamp, string Purpose);

/// <summary>An active grant/lease. <see cref="HeartbeatAt"/> is the grant file's mtime.</summary>
public sealed record Grant(string Prefix, DateTimeOffset GrantedAt, DateTimeOffset HeartbeatAt, DateTimeOffset ClaimTimestamp);

/// <summary>One logged SSH auth attempt.</summary>
public sealed record AttemptLine(DateTimeOffset Timestamp, bool Ok);

/// <summary>The full state of one resource (an account or a slot pool) as read from the ledger.</summary>
public sealed record ResourceState(
    string Env,
    ResourceKind Kind,
    string Name,
    ResourcePolicy Policy,
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<AttemptLine> Attempts);

/// <summary>All resources across all environments in the ledger.</summary>
public sealed record RouterState(IReadOnlyList<ResourceState> Resources);
```

(This references `ResourcePolicy` — implement Task 2 immediately after so the project compiles.)

- [ ] **Step 4: Run test to verify it passes** — after Task 2's `RouterPolicy.cs` exists:

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterResolverTests"`
Expected: PASS (1 test). If Task 2 is not yet done, the project won't compile — proceed to Task 2, then this passes.

- [ ] **Step 5: Commit** (after Task 2 compiles, or commit Task 1+2 together)

```bash
git add src/Styloagent.Core/Router/RouterModel.cs src/Styloagent.Core/Router/RouterDecision.cs tests/Styloagent.Core.Tests/RouterResolverTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): ledger model + decision types

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Resource policy + reader (capacity, lockout, lease)

**Files:**
- Create: `src/Styloagent.Core/Router/RouterPolicy.cs`
- Test: `tests/Styloagent.Core.Tests/RouterPolicyReaderTests.cs`

**Interfaces:**
- Consumes: nothing (base types).
- Produces:
  - `record LockoutPolicy(int Budget, TimeSpan Window, TimeSpan Cooldown)`
  - `record ResourcePolicy(int Capacity, LockoutPolicy? Lockout, TimeSpan LeaseTtl)` with
    `static ResourcePolicy Default => new(1, null, TimeSpan.FromMinutes(2))`.
  - `static ResourcePolicy RouterPolicyReader.Read(string resourceYamlPath)` — tolerant; missing → `Default`.
  - `static TimeSpan RouterPolicyReader.ParseDuration(string, TimeSpan fallback)` — `"10m"`, `"90s"`, `"1h"`.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/RouterPolicyReaderTests.cs`:

```csharp
using System;
using Styloagent.Core.Router;
using Xunit;

public class RouterPolicyReaderTests
{
    [Theory]
    [InlineData("10m", 600)]
    [InlineData("90s", 90)]
    [InlineData("1h", 3600)]
    [InlineData("bad", 42)]     // falls back
    public void ParseDuration_reads_suffixes(string raw, int expectedSeconds)
    {
        var ts = RouterPolicyReader.ParseDuration(raw, TimeSpan.FromSeconds(42));
        Assert.Equal(expectedSeconds, (int)ts.TotalSeconds);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var p = RouterPolicyReader.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Assert.Equal(1, p.Capacity);
        Assert.Null(p.Lockout);
        Assert.Equal(TimeSpan.FromMinutes(2), p.LeaseTtl);
    }

    [Fact]
    public void Reads_capacity_lockout_and_lease()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "res-" + Guid.NewGuid().ToString("N") + ".yaml");
        System.IO.File.WriteAllText(file,
            "capacity: 3\nleaseTtl: 90s\nlockout:\n  budget: 5\n  window: 10m\n  cooldown: 15m\n");
        try
        {
            var p = RouterPolicyReader.Read(file);
            Assert.Equal(3, p.Capacity);
            Assert.Equal(TimeSpan.FromSeconds(90), p.LeaseTtl);
            Assert.NotNull(p.Lockout);
            Assert.Equal(5, p.Lockout!.Budget);
            Assert.Equal(TimeSpan.FromMinutes(10), p.Lockout.Window);
            Assert.Equal(TimeSpan.FromMinutes(15), p.Lockout.Cooldown);
        }
        finally { System.IO.File.Delete(file); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterPolicyReaderTests"`
Expected: FAIL — `RouterPolicy`/`RouterPolicyReader` do not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Router/RouterPolicy.cs`:

```csharp
using System.Globalization;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Router;

/// <summary>Per-account lockout budget: after <see cref="Budget"/> failures within <see cref="Window"/>, cool for <see cref="Cooldown"/>.</summary>
public sealed record LockoutPolicy(int Budget, TimeSpan Window, TimeSpan Cooldown);

/// <summary>A resource's arbitration config. Capacity 1 = exclusive account; N = slot pool. Lockout null = untracked.</summary>
public sealed record ResourcePolicy(int Capacity, LockoutPolicy? Lockout, TimeSpan LeaseTtl)
{
    public static ResourcePolicy Default { get; } = new(Capacity: 1, Lockout: null, LeaseTtl: TimeSpan.FromMinutes(2));
}

[YamlObject]
internal partial class LockoutFile { public int? Budget { get; set; } public string? Window { get; set; } public string? Cooldown { get; set; } }

[YamlObject]
internal partial class ResourceFile { public int? Capacity { get; set; } public string? LeaseTtl { get; set; } public LockoutFile? Lockout { get; set; } }

/// <summary>Tolerant reader for <c>resource.yaml</c>. Missing/invalid → <see cref="ResourcePolicy.Default"/>.</summary>
public static class RouterPolicyReader
{
    public static ResourcePolicy Read(string resourceYamlPath)
    {
        var d = ResourcePolicy.Default;
        try
        {
            if (!File.Exists(resourceYamlPath)) return d;
            var f = YamlSerializer.Deserialize<ResourceFile>(File.ReadAllBytes(resourceYamlPath));
            LockoutPolicy? lockout = f.Lockout is null ? null : new LockoutPolicy(
                Budget: f.Lockout.Budget ?? 5,
                Window: ParseDuration(f.Lockout.Window, TimeSpan.FromMinutes(10)),
                Cooldown: ParseDuration(f.Lockout.Cooldown, TimeSpan.FromMinutes(15)));
            return new ResourcePolicy(
                Capacity: f.Capacity is > 0 ? f.Capacity.Value : 1,
                Lockout: lockout,
                LeaseTtl: ParseDuration(f.LeaseTtl, d.LeaseTtl));
        }
        catch { return d; }
    }

    /// <summary>Parses <c>"10m"</c>/<c>"90s"</c>/<c>"1h"</c> (or bare seconds) to a TimeSpan; fallback on any failure.</summary>
    public static TimeSpan ParseDuration(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        raw = raw.Trim();
        char unit = raw[^1];
        var numPart = char.IsDigit(unit) ? raw : raw[..^1];
        if (!double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return fallback;
        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ when char.IsDigit(unit) => TimeSpan.FromSeconds(n),
            _ => fallback,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterPolicyReaderTests"`
Expected: PASS (5 cases). Then the Task 1 `RouterResolverTests` smoke test also compiles+passes; `dotnet build src/Styloagent.Core -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterPolicy.cs tests/Styloagent.Core.Tests/RouterPolicyReaderTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): resource policy (capacity/lockout/lease) + tolerant reader

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Resolver — FIFO grants up to capacity

**Files:**
- Create: `src/Styloagent.Core/Router/RouterResolver.cs`
- Test: `tests/Styloagent.Core.Tests/RouterResolverTests.cs` (extend)

**Interfaces:**
- Consumes: `RouterState`, `ResourceState`, `Claim`, `Grant`, `RouterDecision`, `ResourcePolicy` (Tasks 1-2).
- Produces: `static IReadOnlyList<RouterDecision> RouterResolver.Resolve(RouterState state, DateTimeOffset now)`.
  This task: for each resource, grant the earliest-timestamped un-granted claims until live grants reach `Capacity`. A claim is "un-granted" if no live grant has its `Prefix`. Ties broken by `Prefix` ordinal. (Lease-expiry and cooldown arrive in Tasks 4-5; for now treat all existing grants as live.)

- [ ] **Step 1: Write the failing test** — add to `RouterResolverTests.cs`:

```csharp
    private static DateTimeOffset T(int sec) => new(2026, 7, 11, 12, 0, sec, TimeSpan.Zero);

    private static ResourceState Account(string name, int capacity,
        Claim[] claims, Grant[] grants) =>
        new("prod", ResourceKind.Account, name, new ResourcePolicy(capacity, null, TimeSpan.FromMinutes(2)),
            claims, grants, System.Array.Empty<AttemptLine>());

    [Fact]
    public void Grants_the_earliest_claim_up_to_capacity()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("docs-", T(5), "x"), new Claim("foss-", T(3), "y") },
            grants: System.Array.Empty<Grant>());
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));

        var grant = Assert.Single(decisions);
        Assert.Equal(RouterAction.Grant, grant.Action);
        Assert.Equal("foss-", grant.Prefix);           // earlier timestamp wins
    }

    [Fact]
    public void Capacity_N_grants_N_and_queues_the_rest()
    {
        var r = Account("ci", capacity: 2,
            claims: new[] { new Claim("a-", T(1), ""), new Claim("b-", T(2), ""), new Claim("c-", T(3), "") },
            grants: System.Array.Empty<Grant>());
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, d => d.Prefix == "a-");
        Assert.Contains(decisions, d => d.Prefix == "b-");
        Assert.DoesNotContain(decisions, d => d.Prefix == "c-");   // queued
    }

    [Fact]
    public void Full_capacity_grants_nothing()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("b-", T(5), "") },
            grants: new[] { new Grant("a-", T(1), T(9), T(0)) });   // held, heartbeat recent
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Empty(decisions);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterResolverTests"`
Expected: FAIL — `RouterResolver` does not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Router/RouterResolver.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterResolverTests"`
Expected: PASS (grant tests + the smoke test).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterResolver.cs tests/Styloagent.Core.Tests/RouterResolverTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): resolver grants FIFO up to capacity

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Resolver — lease expiry + promotion

**Files:**
- Modify: `src/Styloagent.Core/Router/RouterResolver.cs`
- Test: `tests/Styloagent.Core.Tests/RouterResolverTests.cs` (extend)

**Interfaces:**
- Produces: `Resolve` now also emits an `Expire` decision for every grant whose lease has lapsed
  (`now - HeartbeatAt ≥ LeaseTtl`), and the freed capacity is granted to queued claims **in the same pass**.

- [ ] **Step 1: Write the failing test** — add to `RouterResolverTests.cs`:

```csharp
    [Fact]
    public void Expired_grant_is_expired_and_the_queue_head_promoted()
    {
        var r = Account("deploy", capacity: 1,
            claims: new[] { new Claim("b-", T(5), "") },
            grants: new[] { new Grant("a-", T(1), T(1), T(0)) });   // heartbeat at T(1)
        // leaseTtl = 2m; at T(200) => 199s since heartbeat >= 120s => expired
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }),
            new DateTimeOffset(2026, 7, 11, 12, 3, 20, TimeSpan.Zero));

        Assert.Contains(decisions, d => d.Action == RouterAction.Expire && d.Prefix == "a-");
        Assert.Contains(decisions, d => d.Action == RouterAction.Grant && d.Prefix == "b-");
    }

    [Fact]
    public void Live_grant_is_not_expired()
    {
        var r = Account("deploy", capacity: 1,
            claims: System.Array.Empty<Claim>(),
            grants: new[] { new Grant("a-", T(1), T(9), T(0)) });   // heartbeat T(9), now T(10)
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Empty(decisions);
    }
```

- [ ] **Step 2: Run test to verify it fails** — Run the filter; expect FAIL (no Expire decisions emitted yet).

- [ ] **Step 3: Write minimal implementation** — in `Resolve`, before computing `free`, emit expiries and exclude expired grants from `liveGrants` (already done via `IsExpired`); add the expiry decisions:

```csharp
        foreach (var r in state.Resources)
        {
            foreach (var g in r.Grants.Where(g => IsExpired(g, r.Policy.LeaseTtl, now)))
                decisions.Add(new RouterDecision(RouterAction.Expire, r.Env, r.Kind, r.Name, g.Prefix, null));

            var liveGrants = r.Grants.Where(g => !IsExpired(g, r.Policy.LeaseTtl, now)).ToList();
            // …unchanged grant logic below…
        }
```
(The grant loop already ignores expired grants, so freed capacity is granted in the same pass.)

- [ ] **Step 4: Run test to verify it passes** — Run the filter; expect PASS (all resolver tests).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterResolver.cs tests/Styloagent.Core.Tests/RouterResolverTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): resolver expires lapsed leases and promotes the queue

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Resolver — lockout cooldown

**Files:**
- Modify: `src/Styloagent.Core/Router/RouterResolver.cs`
- Test: `tests/Styloagent.Core.Tests/RouterResolverTests.cs` (extend)

**Interfaces:**
- Produces:
  - `static bool RouterResolver.IsCooling(ResourceState r, DateTimeOffset now, out DateTimeOffset until)` — true when the account (`Lockout` non-null) has `≥ Budget` failures since its last success within `Window`; `until` = timestamp of the budget-th such failure + `Cooldown`.
  - `Resolve` grants NOTHING for a resource that `IsCooling` (existing live grants are unaffected; only new grants are withheld).

- [ ] **Step 1: Write the failing test** — add to `RouterResolverTests.cs`:

```csharp
    private static ResourceState AccountWithLockout(string name, AttemptLine[] attempts, Claim[] claims) =>
        new("prod", ResourceKind.Account, name,
            new ResourcePolicy(1, new LockoutPolicy(Budget: 3, Window: TimeSpan.FromMinutes(10), Cooldown: TimeSpan.FromMinutes(15)), TimeSpan.FromMinutes(2)),
            claims, System.Array.Empty<Grant>(), attempts);

    [Fact]
    public void Cooling_account_is_not_granted()
    {
        var attempts = new[] { new AttemptLine(T(1), false), new AttemptLine(T(2), false), new AttemptLine(T(3), false) };
        var r = AccountWithLockout("deploy", attempts, new[] { new Claim("foss-", T(4), "") });
        // 3 fails within window, budget 3, cooldown 15m: cooling until T(3)+15m. At T(10) => cooling.
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.Empty(decisions);
        Assert.True(RouterResolver.IsCooling(r, T(10), out var until));
        Assert.Equal(T(3) + TimeSpan.FromMinutes(15), until);
    }

    [Fact]
    public void A_success_clears_the_failure_streak()
    {
        var attempts = new[] { new AttemptLine(T(1), false), new AttemptLine(T(2), false),
                               new AttemptLine(T(3), true),  new AttemptLine(T(4), false) };  // 1 fail since last ok
        var r = AccountWithLockout("deploy", attempts, new[] { new Claim("foss-", T(5), "") });
        var decisions = RouterResolver.Resolve(new RouterState(new[] { r }), T(10));
        Assert.False(RouterResolver.IsCooling(r, T(10), out _));
        Assert.Single(decisions);                       // granted — not cooling
    }

    [Fact]
    public void Cooldown_lapses_after_the_window()
    {
        var attempts = new[] { new AttemptLine(T(1), false), new AttemptLine(T(2), false), new AttemptLine(T(3), false) };
        var r = AccountWithLockout("deploy", attempts, new[] { new Claim("foss-", T(4), "") });
        var afterCooldown = T(3) + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1);
        Assert.False(RouterResolver.IsCooling(r, afterCooldown, out _));
        Assert.Single(RouterResolver.Resolve(new RouterState(new[] { r }), afterCooldown));
    }
```

- [ ] **Step 2: Run test to verify it fails** — Run the filter; expect FAIL (`IsCooling` missing / cooling not withheld).

- [ ] **Step 3: Write minimal implementation** — add `IsCooling` and gate grants in `Resolve`:

```csharp
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
```

In `Resolve`, inside the per-resource loop, after the expiry emissions and before granting, skip granting when cooling:

```csharp
            if (IsCooling(r, now, out _)) continue;   // withhold new grants; existing grants untouched
```

- [ ] **Step 4: Run test to verify it passes** — Run the filter; expect PASS (all resolver tests). Then the full Core suite: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj` → green.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterResolver.cs tests/Styloagent.Core.Tests/RouterResolverTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): lockout cooldown withholds grants for failing accounts

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `RouterProjection` — read the ledger into `RouterState`

**Files:**
- Create: `src/Styloagent.Core/Router/RouterProjection.cs`
- Test: `tests/Styloagent.Core.Tests/RouterProjectionTests.cs`

**Interfaces:**
- Consumes: `RouterState`, `ResourceState`, `Claim`, `Grant`, `AttemptLine`, `RouterPolicyReader` (Tasks 1-2).
- Produces: `static RouterState RouterProjection.Read(string routerRoot)` — enumerates
  `<routerRoot>/<env>/accounts/<name>/` and `<routerRoot>/<env>/slots/<name>/`; for each, reads
  `resource.yaml` (policy), `claims/*.md` (→ `Claim` via `**From:**`/`**Timestamp:**`/`**Purpose:**`),
  `grants/*.md` (→ `Grant`; `HeartbeatAt` = the file's `File.GetLastWriteTimeUtc`), and `attempts.md`
  (lines `<iso> ok|fail`). Never throws; missing dirs → empty; malformed files skipped.

- [ ] **Step 1: Write the failing test** — create `tests/Styloagent.Core.Tests/RouterProjectionTests.cs`:

```csharp
using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterProjectionTests
{
    [Fact]
    public void Missing_root_is_empty()
        => Assert.Empty(RouterProjection.Read(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N"))).Resources);

    [Fact]
    public void Reads_an_account_with_a_claim_a_grant_and_attempts()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-" + Guid.NewGuid().ToString("N"));
        var acct = Path.Combine(root, "prod", "accounts", "deploy");
        Directory.CreateDirectory(Path.Combine(acct, "claims"));
        Directory.CreateDirectory(Path.Combine(acct, "grants"));
        File.WriteAllText(Path.Combine(acct, "resource.yaml"), "capacity: 1\nlockout:\n  budget: 5\n  window: 10m\n  cooldown: 15m\n");
        File.WriteAllText(Path.Combine(acct, "claims", "2026-07-11T120003Z-foss-.md"),
            "**From:** foss-\n**Timestamp:** 2026-07-11T12:00:03Z\n**Purpose:** deploy\n");
        File.WriteAllText(Path.Combine(acct, "grants", "docs-.md"),
            "**Holder:** docs-\n**Granted:** 2026-07-11T12:00:04Z\n**Expires:** 2026-07-11T12:02:04Z\n**ClaimTimestamp:** 2026-07-11T12:00:01Z\n");
        File.WriteAllText(Path.Combine(acct, "attempts.md"), "2026-07-11T12:00:05Z ok\n2026-07-11T12:03:11Z fail\n");
        try
        {
            var state = RouterProjection.Read(root);
            var r = Assert.Single(state.Resources);
            Assert.Equal("prod", r.Env);
            Assert.Equal(ResourceKind.Account, r.Kind);
            Assert.Equal("deploy", r.Name);
            Assert.Equal(1, r.Policy.Capacity);
            Assert.Contains(r.Claims, c => c.Prefix == "foss-" && c.Purpose == "deploy");
            Assert.Contains(r.Grants, g => g.Prefix == "docs-");
            Assert.Equal(2, r.Attempts.Count);
            Assert.Contains(r.Attempts, a => !a.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterProjectionTests"`
Expected: FAIL — `RouterProjection` does not exist.

- [ ] **Step 3: Write minimal implementation** — create `src/Styloagent.Core/Router/RouterProjection.cs`:

```csharp
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
                var granted = ParseTs(GrantedRx().Match(body)) ?? File.GetLastWriteTimeUtc(f);
                var claimTs = ParseTs(ClaimTsRx().Match(body)) ?? granted;
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
                if (!DateTimeOffset.TryParse(sp[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
                list.Add(new AttemptLine(ts, sp[1].Trim().Equals("ok", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }
        return list;
    }

    private static DateTimeOffset? ParseTs(Match m)
        => m.Success && DateTimeOffset.TryParse(m.Groups[1].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts) ? ts : null;

    // GetLastWriteTimeUtc returns a DateTime(Kind=Utc); wrap as DateTimeOffset for granted/claimTs fallbacks.
    private static DateTimeOffset ToOffset(DateTime utc) => new(utc, TimeSpan.Zero);
}
```
(Fix the one `granted` fallback: `?? new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)` — use `ToOffset` for both fallbacks; adjust the two `?? File.GetLastWriteTimeUtc(f)` lines to `?? ToOffset(File.GetLastWriteTimeUtc(f))` so the types are `DateTimeOffset`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterProjectionTests"`
Expected: PASS (2 tests). Then the full Core suite + `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterProjection.cs tests/Styloagent.Core.Tests/RouterProjectionTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): projection reads the markdown ledger into RouterState

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Full Core suite green

**Files:** none (verification).

- [ ] **Step 1:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 2:** `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj` → all pass (router + existing).
- [ ] **Step 3:** Commit any incidental fixes with the standard trailer.

---

## Self-Review

**Spec coverage (Plan A slice — the engine):**
- Advisory ledger model (Claim/Grant/AttemptLine/Resource/RouterState) → Task 1. ✓
- Resource policy: capacity, lockout budget/window/cooldown, leaseTtl + tolerant reader → Task 2. ✓
- Pure resolver: FIFO grants up to capacity → Task 3; lease-expiry + promotion → Task 4; lockout cooldown (derived, no marker) → Task 5. ✓
- `RouterProjection` reads the ledger (claims/grants/attempts/resource.yaml; mtime as heartbeat) → Task 6. ✓
- Capacity unifies Account (1) + Slot (N) → the resolver is capacity-driven; `ResourceKind` distinguishes for the ledger path only. ✓
- No I/O / no ambient clock in the resolver; `now` injected everywhere → all tasks. ✓
- **Deferred to Plan B:** `RouterCoordinator` (applies decisions, file-watch, notifications via the bus), `RouterTools` (MCP: claim/heartbeat/release/log_attempt/router_status), `ProjectConfig.RouterRoot`, the Router panel tab, and the disposition/queue-position query the panel shows.

**Deviation (intentional, noted):** the spec floated a `# cooldown-until` marker line in `attempts.md`; this plan derives cooldown purely from the attempt log + `now` in `IsCooling` (no marker to write/read), which is simpler and equally deterministic. The projection still tolerantly skips any `#`-comment lines it encounters.

**Placeholder scan:** every step carries complete code. The one cross-file dependency (Task 1's `RouterModel` references Task 2's `ResourcePolicy`) is called out explicitly with "do Task 1 and 2 back-to-back."

**Type consistency:** `RouterState`/`ResourceState`/`Claim`/`Grant`/`AttemptLine`/`ResourcePolicy` (Tasks 1-2) are consumed identically by `RouterResolver` (Tasks 3-5) and `RouterProjection` (Task 6); `RouterDecision(RouterAction, Env, Kind, Name, Prefix, Expires?)` is produced by the resolver and will be consumed by Plan B's coordinator; `RouterResolver.Resolve(RouterState, DateTimeOffset)` and `IsCooling(ResourceState, DateTimeOffset, out DateTimeOffset)` signatures are stable across Tasks 3-5.
```
