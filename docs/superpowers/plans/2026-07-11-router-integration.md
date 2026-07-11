# SSH/Shell Router — Coordinator + Tools + Panel (Plan B of B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the router usable: a `RouterCoordinator` that applies the resolver's decisions to the ledger and emits notifications, MCP `RouterTools` (`claim`/`heartbeat`/`release`/`log_attempt`/`router_status`) for agents, and a Router panel showing live holders/queues/cooldowns.

**Architecture:** Plan A's pure engine (`RouterResolver`/`RouterProjection`) is driven by a `RouterCoordinator.Tick(root, now)` that reads state, resolves, and applies Grant/Expire decisions via `RouterWriter` (writes/deletes grant files, appends `log.md`) — emitting a bus message per transition. Agents drop claims / heartbeats / attempts via `RouterClient` (thin file ops), exposed as MCP tools through an `IRouterController` seam (mirrors `IFleetController`). A `RouterViewModel`/`RouterView` renders the projection in a new tab.

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, ModelContextProtocol.AspNetCore, xUnit.

## Global Constraints

- net10.0; `<Nullable>enable</Nullable>`; analyzers AS ERRORS; build clean (0 warnings/0 errors; pre-existing NU1903 warnings are not ours).
- The router **engine stays pure** (Plan A). The new `RouterWriter`/`RouterCoordinator`/`RouterClient` do I/O and are **tolerant (never throw)** like `RouterProjection`. `now` is still injected into `Tick` (no ambient clock in the tick logic).
- **Single writer of grants:** only the `RouterCoordinator` writes/deletes grant files. Agents write only their own claim/attempt files and `touch` their grant (heartbeat).
- MCP tool names use underscores → wrap with `#pragma warning disable CA1707` / `[SuppressMessage("Style","CA1707",…)]` (see `FleetTools`).
- ConfigureAwait: components blocked-on from the UI thread use `.ConfigureAwait(false)`; VM methods that set UI-bound state and are awaited (not blocked-on) do not.
- Commit directly to `main` (no new branch), authored `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "<subject>` ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**Create:**
- `src/Styloagent.Core/Router/RouterPaths.cs` — path helpers (`kind → "accounts"/"slots"`, resource dir, grant/claim/attempts/log paths).
- `src/Styloagent.Core/Router/RouterWriter.cs` — write/delete grant files, append `log.md` (coordinator-only).
- `src/Styloagent.Core/Router/RouterClient.cs` — agent-side file ops: `DropClaim`/`Heartbeat`/`Release`/`LogAttempt`; kind auto-detect.
- `src/Styloagent.Core/Router/RouterCoordinator.cs` — `Tick(root, now)` → applied decisions; pure orchestration over projection/resolver/writer.
- `src/Styloagent.Core/Mcp/IRouterController.cs` — the MCP seam.
- `src/Styloagent.App/Mcp/RouterController.cs` — implements `IRouterController` over the VM's project router root.
- `src/Styloagent.App/Mcp/RouterTools.cs` — the 5 MCP tools.
- `src/Styloagent.App/Router/RouterHost.cs` — timer + `FileSystemWatcher` lifecycle driving `Tick`, + bus notifications.
- `src/Styloagent.App/ViewModels/RouterViewModel.cs`, `src/Styloagent.App/Views/RouterView.axaml` (+`.axaml.cs`).
- Tests: `RouterWriterTests`, `RouterClientTests`, `RouterCoordinatorTests` (Core.Tests); `RouterToolsTests` (App.Tests); `RouterViewTests` (UITests).

**Modify:**
- `src/Styloagent.Core/Projects/ProjectConfig.cs` — add `RouterRoot`.
- `src/Styloagent.App/Mcp/StyloagentMcpServer.cs` — register `RouterTools` + `IRouterController`.
- `src/Styloagent.App/ViewModels/MainWindowViewModel.cs` — start the `RouterHost`, expose `Router`, build the controller.
- `src/Styloagent.App/Views/MainWindow.axaml` — add the Router tab.

---

## Task 1: `RouterPaths` + `RouterWriter`

**Files:**
- Create: `src/Styloagent.Core/Router/RouterPaths.cs`, `src/Styloagent.Core/Router/RouterWriter.cs`
- Test: `tests/Styloagent.Core.Tests/RouterWriterTests.cs`

**Interfaces:**
- Produces:
  - `static class RouterPaths`: `string KindDir(ResourceKind)` (`Account→"accounts"`, `Slot→"slots"`); `string ResourceDir(root, env, kind, name)`; `string GrantsDir/ClaimsDir(...)`; `string GrantFile(root, env, kind, name, prefix)`; `string AttemptsFile(...)`; `string LogFile(...)`.
  - `static class RouterWriter`: `void WriteGrant(root, env, kind, name, prefix, DateTimeOffset granted, DateTimeOffset expires, DateTimeOffset claimTs)`; `void DeleteGrant(root, env, kind, name, prefix)`; `void AppendLog(root, env, kind, name, string line)`. All tolerant (never throw).

- [ ] **Step 1: Write the failing test** — `tests/Styloagent.Core.Tests/RouterWriterTests.cs`:

```csharp
using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterWriterTests
{
    [Fact]
    public void WriteGrant_then_DeleteGrant_round_trips()
    {
        var root = Path.Combine(Path.GetTempPath(), "rw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var granted = new DateTimeOffset(2026, 7, 11, 12, 0, 4, TimeSpan.Zero);
            RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
                granted, granted + TimeSpan.FromMinutes(2), granted);
            var file = RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-");
            Assert.True(File.Exists(file));
            Assert.Contains("**Holder:** foss-", File.ReadAllText(file));

            RouterWriter.DeleteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-");
            Assert.False(File.Exists(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AppendLog_creates_and_appends()
    {
        var root = Path.Combine(Path.GetTempPath(), "rw-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterWriter.AppendLog(root, "prod", ResourceKind.Slot, "ci", "granted foss-");
            var log = File.ReadAllText(RouterPaths.LogFile(root, "prod", ResourceKind.Slot, "ci"));
            Assert.Contains("granted foss-", log);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.Core.Tests/Styloagent.Core.Tests.csproj --filter "FullyQualifiedName~RouterWriterTests"` → FAIL.

- [ ] **Step 3: Implement** — `src/Styloagent.Core/Router/RouterPaths.cs`:

```csharp
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
```

`src/Styloagent.Core/Router/RouterWriter.cs`:

```csharp
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
```

> Note: `AppendLog` uses `DateTimeOffset.UtcNow` for the log line stamp — this is a **write-side audit stamp**, not arbitration logic, so it is acceptable here (the ban on ambient clock applies to the pure resolver/tick decision logic, not to audit logging). Keep decision-affecting time (`Tick`) injected.

- [ ] **Step 4: Run to verify it passes** — the filter → PASS; `dotnet build src/Styloagent.Core -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterPaths.cs src/Styloagent.Core/Router/RouterWriter.cs tests/Styloagent.Core.Tests/RouterWriterTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): ledger path helpers + grant/log writer

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `RouterCoordinator.Tick`

**Files:**
- Create: `src/Styloagent.Core/Router/RouterCoordinator.cs`
- Test: `tests/Styloagent.Core.Tests/RouterCoordinatorTests.cs`

**Interfaces:**
- Consumes: `RouterProjection`, `RouterResolver`, `RouterWriter`, `RouterState`, `RouterDecision` (Plan A + Task 1).
- Produces: `static IReadOnlyList<RouterDecision> RouterCoordinator.Tick(string routerRoot, DateTimeOffset now)` —
  reads state, resolves, applies each decision (Grant → `WriteGrant` with the claim's timestamp from state; Expire → `DeleteGrant`), appends a `log.md` line per decision, and returns the applied decisions (for the host to notify on). Tolerant.

- [ ] **Step 1: Write the failing test** — `tests/Styloagent.Core.Tests/RouterCoordinatorTests.cs`:

```csharp
using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterCoordinatorTests
{
    [Fact]
    public void Tick_grants_a_pending_claim()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        var claims = RouterPaths.ClaimsDir(root, "prod", ResourceKind.Account, "deploy");
        Directory.CreateDirectory(claims);
        File.WriteAllText(RouterPaths.ResourceDir(root, "prod", ResourceKind.Account, "deploy") + "/resource.yaml", "capacity: 1\n");
        File.WriteAllText(Path.Combine(claims, "2026-07-11T120003Z-foss-.md"),
            "**From:** foss-\n**Timestamp:** 2026-07-11T12:00:03Z\n**Purpose:** deploy\n");
        try
        {
            var applied = RouterCoordinator.Tick(root, new DateTimeOffset(2026, 7, 11, 12, 0, 10, TimeSpan.Zero));
            Assert.Contains(applied, d => d.Action == RouterAction.Grant && d.Prefix == "foss-");
            Assert.True(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Tick_expires_a_stale_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 11, 12, 2, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(RouterPaths.ResourceDir(root, "prod", ResourceKind.Account, "deploy") + "/resource.yaml", "capacity: 1\nleaseTtl: 2m\n");
        // Force the grant file's mtime far in the past so the lease is stale.
        File.SetLastWriteTimeUtc(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-"),
            new DateTime(2026, 7, 11, 11, 0, 0, DateTimeKind.Utc));
        try
        {
            var applied = RouterCoordinator.Tick(root, new DateTimeOffset(2026, 7, 11, 12, 0, 10, TimeSpan.Zero));
            Assert.Contains(applied, d => d.Action == RouterAction.Expire && d.Prefix == "foss-");
            Assert.False(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 2: Run to verify it fails** — filter → FAIL (`RouterCoordinator` missing).

- [ ] **Step 3: Implement** — `src/Styloagent.Core/Router/RouterCoordinator.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes** — filter → PASS (2 tests); full Core.Tests → green.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Router/RouterCoordinator.cs tests/Styloagent.Core.Tests/RouterCoordinatorTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): coordinator applies grant/expire decisions to the ledger

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `ProjectConfig.RouterRoot` + `RouterClient`

**Files:**
- Modify: `src/Styloagent.Core/Projects/ProjectConfig.cs`
- Create: `src/Styloagent.Core/Router/RouterClient.cs`
- Test: `tests/Styloagent.Core.Tests/RouterClientTests.cs`

**Interfaces:**
- Produces:
  - `ProjectConfig.RouterRoot` = `<cfg>/router`.
  - `static class RouterClient` (agent-side, tolerant):
    - `ResourceKind DetectKind(root, env, name)` — `Slot` if `slots/<name>/` exists, else `Account`.
    - `string DropClaim(root, env, name, prefix, purpose, DateTimeOffset ts)` — writes `claims/<iso>-<prefix>.md`; returns the file path.
    - `bool Heartbeat(root, env, name, prefix)` — `touch`es the grant file's mtime; returns whether a grant existed.
    - `void Release(root, env, name, prefix)` — deletes the grant file + the caller's claim file(s).
    - `void LogAttempt(root, env, account, bool ok, DateTimeOffset ts)` — appends `<iso> ok|fail` to the account's `attempts.md`.

- [ ] **Step 1: Modify `ProjectConfig`** — append `string RouterRoot` (after `GitPolicyPath`) and set `RouterRoot: Path.Combine(cfg, "router")` in `For`.

- [ ] **Step 2: Write the failing test** — `tests/Styloagent.Core.Tests/RouterClientTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using Styloagent.Core.Router;
using Xunit;

public class RouterClientTests
{
    [Fact]
    public void DropClaim_writes_a_claim_the_projection_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcl-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterClient.DropClaim(root, "prod", "deploy", "foss-", "ship it",
                new DateTimeOffset(2026, 7, 11, 12, 0, 3, TimeSpan.Zero));
            var state = RouterProjection.Read(root);
            var r = Assert.Single(state.Resources);
            Assert.Equal(ResourceKind.Account, r.Kind);
            Assert.Contains(r.Claims, c => c.Prefix == "foss-" && c.Purpose == "ship it");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LogAttempt_appends_and_Release_removes_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcl-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterClient.LogAttempt(root, "prod", "deploy", ok: false, new DateTimeOffset(2026, 7, 11, 12, 0, 5, TimeSpan.Zero));
            var attempts = File.ReadAllText(RouterPaths.AttemptsFile(root, "prod", ResourceKind.Account, "deploy"));
            Assert.Contains("fail", attempts);

            RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            RouterClient.Release(root, "prod", "deploy", "foss-");
            Assert.False(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 3: Run to verify it fails** — filter → FAIL.

- [ ] **Step 4: Implement** — `src/Styloagent.Core/Router/RouterClient.cs`:

```csharp
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
```

- [ ] **Step 5: Run + commit** — filter → PASS; full Core.Tests → green; `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Core/Projects/ProjectConfig.cs src/Styloagent.Core/Router/RouterClient.cs tests/Styloagent.Core.Tests/RouterClientTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): RouterClient (claim/heartbeat/release/log-attempt) + ProjectConfig.RouterRoot

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `IRouterController` + `RouterTools` (MCP)

**Files:**
- Create: `src/Styloagent.Core/Mcp/IRouterController.cs`, `src/Styloagent.App/Mcp/RouterController.cs`, `src/Styloagent.App/Mcp/RouterTools.cs`
- Modify: `src/Styloagent.App/Mcp/StyloagentMcpServer.cs`
- Test: `tests/Styloagent.App.Tests/RouterToolsTests.cs`

**Interfaces:**
- Produces:
  - `interface IRouterController` (Core.Mcp): `Task<string> ClaimAsync(string caller, string env, string resource, string purpose)`, `Task<string> HeartbeatAsync(string caller, string env, string resource)`, `Task<string> ReleaseAsync(string caller, string env, string resource)`, `Task<string> LogAttemptAsync(string caller, string env, string account, bool ok)`, `Task<string> StatusAsync(string? env)`. (Returns human/agent-readable strings.)
  - `RouterController(MainWindowViewModel vm) : IRouterController` — resolves `_project.RouterRoot`, calls `RouterClient`/`RouterProjection`, marshals via `Dispatcher.UIThread` where needed.
  - `RouterTools` (App.Mcp) — 5 `[McpServerTool]`s (`claim`/`heartbeat`/`release`/`log_attempt`/`router_status`), auth-guarded like `FleetTools`; caller prefix from `McpAuth.CallerPrefix`.

- [ ] **Step 1: Write the failing test** — `tests/Styloagent.App.Tests/RouterToolsTests.cs` (fake controller records calls, mirrors `FleetToolsTests`):

```csharp
using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class RouterToolsTests
{
    private sealed class FakeRouter : IRouterController
    {
        public string? LastClaimCaller, LastEnv, LastResource;
        public Task<string> ClaimAsync(string caller, string env, string resource, string purpose)
        { LastClaimCaller = caller; LastEnv = env; LastResource = resource; return Task.FromResult($"claimed {resource}"); }
        public Task<string> HeartbeatAsync(string caller, string env, string resource) => Task.FromResult("ok");
        public Task<string> ReleaseAsync(string caller, string env, string resource) => Task.FromResult("released");
        public Task<string> LogAttemptAsync(string caller, string env, string account, bool ok) => Task.FromResult("logged");
        public Task<string> StatusAsync(string? env) => Task.FromResult("prod/deploy: held by foss-");
    }

    private static IHttpContextAccessor Acc(string? agent, string? auth)
    {
        var ctx = new DefaultHttpContext();
        if (agent is not null) ctx.Request.Headers[McpAuth.AgentHeader] = agent;
        if (auth is not null) ctx.Request.Headers["Authorization"] = auth;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public async Task claim_uses_caller_prefix_and_returns_disposition()
    {
        var ctrl = new FakeRouter();
        var tools = new RouterTools(Acc("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));
        var result = await tools.claim("prod", "deploy", "ship it");
        Assert.Equal("foss-", ctrl.LastClaimCaller);
        Assert.Equal("deploy", ctrl.LastResource);
        Assert.Contains("claimed deploy", result);
    }

    [Fact]
    public async Task claim_refuses_a_bad_token()
    {
        var ctrl = new FakeRouter();
        var tools = new RouterTools(Acc("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.claim("prod", "deploy", "x");
        Assert.Null(ctrl.LastClaimCaller);
        Assert.Contains("unauthorized", result);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Styloagent.App.Tests/Styloagent.App.Tests.csproj --filter "FullyQualifiedName~RouterToolsTests"` → FAIL.

- [ ] **Step 3: Implement**
- `IRouterController.cs` (Core.Mcp) — the interface above.
- `RouterTools.cs` (App.Mcp) — mirror `FleetTools`: ctor `(IHttpContextAccessor http, IRouterController controller, McpAuth auth)`; each tool inside `#pragma warning disable CA1707`, auth-guard (`ctx is null || !_auth.TokenOk(ctx)` → `"unauthorized"`; caller null → `"unauthorized: missing caller identity"`), then delegate to the controller. `router_status` may accept an optional `env` (pass `""`→null). `log_attempt(env, account, ok)` and `claim(env, resource, purpose)` etc. exact signatures matching the tests.
- `RouterController.cs` (App.Mcp) — `ClaimAsync` etc. resolve the VM's `_project?.RouterRoot`; if null return `"no active project"`. `ClaimAsync` → `RouterClient.DropClaim(root, env, resource, caller, purpose, DateTimeOffset.Now)` then return a disposition string derived from `RouterProjection.Read`+`RouterResolver` (e.g. "queued at position N" / "claim recorded — poll router_status"). `HeartbeatAsync`→`RouterClient.Heartbeat`; `ReleaseAsync`→`RouterClient.Release`; `LogAttemptAsync`→`RouterClient.LogAttempt`; `StatusAsync`→ format `RouterProjection.Read(root)` holders/queues/cooldowns. Add a `MainWindowViewModel.RouterRootOrNull` accessor (returns `_project?.RouterRoot`) for the controller, or pass the VM and read via a public method.
- `StyloagentMcpServer.cs` — add an `IRouterController router` parameter to `StartAsync`, `builder.Services.AddSingleton<IRouterController>(router)`, and `.WithTools<RouterTools>()` alongside `FleetTools`. Update the call site in `MainWindowViewModel.StartFleetServerAsync` to `StartAsync(new FleetController(this), new RouterController(this))`.

- [ ] **Step 4: Run + commit** — focused test → PASS; full App.Tests → green; `dotnet build styloagent.sln -clp:ErrorsOnly` → 0 errors.

```bash
git add src/Styloagent.Core/Mcp/IRouterController.cs src/Styloagent.App/Mcp/RouterController.cs src/Styloagent.App/Mcp/RouterTools.cs src/Styloagent.App/Mcp/StyloagentMcpServer.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/RouterToolsTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): MCP RouterTools (claim/heartbeat/release/log_attempt/status)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `RouterHost` — tick + watch + notifications, wired into the app

**Files:**
- Create: `src/Styloagent.App/Router/RouterHost.cs`
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Styloagent.App.Tests/RouterHostTests.cs`

**Interfaces:**
- Produces: `RouterHost : IDisposable` — `RouterHost(string routerRoot, Action<RouterDecision> onDecision)`; a `System.Threading.Timer` calls `RouterCoordinator.Tick(root, DateTimeOffset.UtcNow)` on an interval (e.g. 2s), and a debounced `FileSystemWatcher` on `routerRoot` triggers an out-of-band tick; each applied decision is passed to `onDecision`. Tolerant; `Dispose` stops both. The VM subscribes `onDecision` → drop a bus message on grant/expire.

- [ ] **Step 1: Write the failing test** — `tests/Styloagent.App.Tests/RouterHostTests.cs`: create a temp ledger with a pending claim (as in `RouterCoordinatorTests`), start a `RouterHost` with a callback that signals a `ManualResetEventSlim`, assert the callback fires (a Grant decision) within a timeout; `Dispose`. (Use the timer path; a 2 s interval + a 5 s wait.)

- [ ] **Step 2: Run to verify it fails** — filter → FAIL.

- [ ] **Step 3: Implement** — `RouterHost.cs`: a `System.Threading.Timer` (due 0, period 2 s) whose callback runs `foreach (var d in RouterCoordinator.Tick(_root, DateTimeOffset.UtcNow)) _onDecision(d);` inside a try/catch; a `FileSystemWatcher` on `_root` (`IncludeSubdirectories = true`, `NotifyFilter` LastWrite|FileName) that, debounced (~300 ms via a reset `Timer`), triggers the same tick; a `volatile bool _disposed` guard so no callback fires after `Dispose`; `Dispose` disposes both timers + the watcher. (Mirror the Plan 2c `WorktreeGitWatcher` lock/dispose discipline.)
- VM wiring: in `InitializeAsync` (after the project is known), create `_routerHost = new RouterHost(project.RouterRoot, d => Dispatcher.UIThread.Post(() => OnRouterDecision(d)))`, where `OnRouterDecision` drops a bus message (reuse the channel writer used by the delivery system) — e.g. an info message to the affected prefix: "granted <resource>" / "your grant on <resource> expired". Dispose `_routerHost` in the VM `Dispose()`. (If dropping a bus message is non-trivial from here, a minimal implementation just refreshes the Router panel — see Task 6 — and logs; note it.)

- [ ] **Step 4: Run + commit** — filter → PASS (or skips cleanly if FS-watch unsupported); full App.Tests → green.

```bash
git add src/Styloagent.App/Router/RouterHost.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs tests/Styloagent.App.Tests/RouterHostTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): RouterHost drives the coordinator (tick + .git-style watch) + notifies

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Router panel (ViewModel + View + tab)

**Files:**
- Create: `src/Styloagent.App/ViewModels/RouterViewModel.cs`, `src/Styloagent.App/Views/RouterView.axaml` (+`.axaml.cs`)
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`, `src/Styloagent.App/Views/MainWindow.axaml`
- Test: `tests/Styloagent.UITests/RouterViewTests.cs`

**Interfaces:**
- Produces:
  - `RouterViewModel(string routerRoot)` with `ObservableCollection<RouterResourceRow> Resources` + `[RelayCommand] Refresh()` that reads `RouterProjection.Read(root)` and, using `RouterResolver.IsCooling`, builds display rows: `RouterResourceRow(string Env, string Name, string Kind, string Holders, int QueueDepth, string Cooldown)`.
  - `MainWindowViewModel.Router` (a `RouterViewModel`), created in `InitializeAsync`; `RouterHost` (Task 5) triggers `Router.Refresh()` on decisions.

- [ ] **Step 1: Write the failing test** — `tests/Styloagent.UITests/RouterViewTests.cs` mirroring `IssuesViewTests`: build a temp ledger (a held account + a queued claim), `new RouterViewModel(root)`, `Refresh()`, host `RouterView`, `SettleAsync`, assert the resource name + holder render, screenshot `/tmp/styloagent-router.png`.

- [ ] **Step 2: Run to verify it fails** — filter → FAIL.

- [ ] **Step 3: Implement** — `RouterViewModel` (build rows from the projection; `Holders` = comma-joined live grant prefixes; `QueueDepth` = un-granted claims count; `Cooldown` = `IsCooling` → "cooling until …" else ""); `RouterView.axaml` (an `ItemsControl`/`DataGrid`-ish list over `Resources`, theme tokens, empty-state "No environments configured"); add a **Router** tab to `MainWindow.axaml` (icon e.g. `Globe`/`Server`/`Flowchart`) hosting `<views:RouterView DataContext="{Binding Router}" />`. Wire `Router` construction + the `RouterHost` refresh hook in the VM.

- [ ] **Step 4: Run + commit** — render test → PASS; inspect `/tmp/styloagent-router.png`; full App.Tests + UITests → green.

```bash
git add src/Styloagent.App/ViewModels/RouterViewModel.cs src/Styloagent.App/Views/RouterView.axaml src/Styloagent.App/Views/RouterView.axaml.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/Views/MainWindow.axaml tests/Styloagent.UITests/RouterViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(router): Router panel — live holders/queues/cooldowns

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Full-suite green + router screenshot + prompt/docs

**Files:**
- Modify: `src/Styloagent.Core/Projects/DefaultTemplates.cs` (document the router tools in the overview agent prompt)

- [ ] **Step 1:** Add a short "Environment routing" note to `DefaultTemplates.SystemPrompt` tools list: agents that need SSH/deploy access call `claim(env, resource, purpose)` → poll `router_status` → connect → `log_attempt` per auth → `heartbeat` while working → `release`; the router serialises access and prevents lockouts.
- [ ] **Step 2:** `dotnet build styloagent.sln -clp:ErrorsOnly` → `0 Error(s)`.
- [ ] **Step 3:** run Core, App, Git, UITests suites → all pass.
- [ ] **Step 4:** inspect `/tmp/styloagent-router.png` — the panel renders resources with holders/queues.
- [ ] **Step 5:** commit the prompt update + any incidental fixes with the standard trailer.

---

## Self-Review

**Spec coverage (Plan B slice — the integration):**
- `RouterCoordinator` applies decisions + logs → Tasks 1-2. ✓
- Agent-side claim/heartbeat/release/log_attempt → Task 3 (`RouterClient`). ✓
- MCP tools (claim/heartbeat/release/log_attempt/router_status) → Task 4. ✓
- Lifecycle: tick + `.git`-style watch, single grant-writer, notifications via the bus → Task 5. ✓
- Router panel (live holders/queues/cooldowns/slots) → Task 6. ✓
- `ProjectConfig.RouterRoot` (`.styloagent/router`) → Task 3. ✓
- Prompt/docs so an agent can follow the flow → Task 7. ✓
- Capacity (slot vs account) auto-detected from the ledger dir → Task 3 (`DetectKind`). ✓

**Deviations (intentional, noted):** `RouterWriter.AppendLog` uses `DateTimeOffset.UtcNow` for the audit-line stamp — the ambient-clock ban is on decision logic (`Tick`/resolver), which still take injected `now`; audit stamps are write-side. The `claim` tool returns a "poll router_status" disposition rather than blocking for a grant (the coordinator grants on its next ~2 s tick), keeping the tool thin and the coordinator the single grant-writer.

**Placeholder scan:** the writer/coordinator/client are complete code; the MCP + host + panel tasks specify exact signatures and the shared-instance/auth patterns already used by `FleetTools`/`FleetController`. The one soft spot (dropping a bus message from `OnRouterDecision`) is flagged with a minimal fallback (refresh the panel + log).

**Type consistency:** `RouterPaths`/`RouterWriter` (Task 1) are used by `RouterCoordinator` (Task 2) and `RouterClient` (Task 3); `IRouterController` (Task 4) is consumed by `RouterTools` (Task 4) and implemented by `RouterController`; `RouterCoordinator.Tick(root, now) → IReadOnlyList<RouterDecision>` (Task 2) is driven by `RouterHost` (Task 5); `RouterViewModel`/`RouterResourceRow` (Task 6) read the same projection.
