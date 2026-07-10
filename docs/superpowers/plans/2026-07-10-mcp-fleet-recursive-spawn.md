# MCP Fleet — Recursive Self-Assembly Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the overview agent (and every child) launch its own subsystems via an in-process MCP server exposing `spawn_agent` + `list_fleet`, with autonomous-but-bounded recursion.

**Architecture:** Styloagent hosts a localhost HTTP MCP server (`ModelContextProtocol.AspNetCore` / Kestrel, ephemeral port). Each `claude` is launched with a `--mcp-config` naming that server, with the caller's **prefix** in an `X-Styloagent-Agent` header. Tool calls arrive in-process, are marshalled to the UI thread, consult a pure `FleetGovernor`, and drive the existing pane-creation path with parent/depth tracking. A global Pause + fleet/depth caps bound the recursion.

**Tech Stack:** .NET 10 · Avalonia 11.3 · CommunityToolkit.Mvvm · VYaml · `ModelContextProtocol.AspNetCore` · ASP.NET Core (Kestrel) · xUnit.

## Global Constraints

- Builds on the orchestration-bootstrap slice; reuses `ProjectConfig`/`ProjectScaffolder`, `AgentManifestEntry`, `AgentSession`, `HookArgs`, `PresentationStore.DefaultColorFor`, and the existing `SpawnProposed`/`AddAgent` pane path.
- **Caller identity is the prefix** (live prefixes are unique via the `DuplicatePrefix` guard). The hook id stays the launcher's concern; MCP identity is the prefix.
- Governor decisions are **structured results**, never thrown exceptions. Reasons: `FleetFull`, `MaxDepth`, `Paused`, `DuplicatePrefix`, `InvalidPrefix`, `UnknownParent`.
- Depth: overview is depth 0; a child's depth = parent depth + 1.
- Guardrail defaults: `MaxFleet=12`, `MaxDepth=3`, in `.styloagent/fleet.yaml` (tolerant reader, defaults on missing/invalid, never throws).
- Server binds **loopback only**, ephemeral port, per-run bearer token. On start failure agents launch WITHOUT the mcp-config (no-spawn degrade); the app still works.
- The repo's `.editorconfig` treats many CA rules as ERRORS — run `dotnet build` and fix every `error CA####`.
- All app mutations marshalled to the UI thread. No real `claude` spawned in tests (use fakes).
- Commit each task with `git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "..."` ending with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Work on `main`; do not branch.

---

### Task 1: Core fleet types + FleetGovernor + IFleetController

**Files:**
- Create: `src/Styloagent.Core/Mcp/FleetTypes.cs`, `src/Styloagent.Core/Mcp/FleetGovernor.cs`, `src/Styloagent.Core/Mcp/IFleetController.cs`
- Test: `tests/Styloagent.Core.Tests/FleetGovernorTests.cs`

**Interfaces:**
- Produces: `RejectReason` enum; `FleetMember(string Prefix, string Responsibility, string? ParentPrefix, int Depth, string State)`; `FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused)`; `FleetSnapshot(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused)`; `SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt)`; `SpawnOutcome(bool Spawned, string? Prefix, RejectReason? Reason, string Message)` with statics `Ok(string prefix)` and `Reject(RejectReason, string)`; `Decision(bool Allowed, RejectReason? Reason, string Message)` with statics `Allow()` and `Deny(RejectReason, string)`; `FleetGovernor.Check(FleetState state, string parentPrefix, string newPrefix) : Decision`; `interface IFleetController { Task<SpawnOutcome> SpawnAsync(SpawnRequest req); FleetSnapshot Snapshot(); }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/FleetGovernorTests.cs`:

```csharp
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.Core.Tests;

public class FleetGovernorTests
{
    private static FleetMember M(string prefix, string? parent, int depth)
        => new(prefix, "resp", parent, depth, "running");

    private static FleetState State(int maxFleet, int maxDepth, bool paused, params FleetMember[] members)
        => new(members, maxFleet, maxDepth, paused);

    [Fact]
    public void Allows_a_spawn_under_all_limits()
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Rejects_when_fleet_is_full()
    {
        var members = new List<FleetMember> { M("overview-", null, 0) };
        for (int i = 0; i < 11; i++) members.Add(M($"a{i}-", "overview-", 1));
        var s = new FleetState(members, 12, 3, false);   // 12 members already
        var d = FleetGovernor.Check(s, "overview-", "new-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.FleetFull, d.Reason);
    }

    [Fact]
    public void Rejects_beyond_max_depth()
    {
        // parent at depth 3, MaxDepth 3 → child would be depth 4
        var s = State(12, 3, false, M("overview-", null, 0), M("deep-", "overview-", 3));
        var d = FleetGovernor.Check(s, "deep-", "deeper-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.MaxDepth, d.Reason);
    }

    [Fact]
    public void Rejects_when_paused()
    {
        var s = State(12, 3, true, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.Paused, d.Reason);
    }

    [Fact]
    public void Rejects_duplicate_live_prefix()
    {
        var s = State(12, 3, false, M("overview-", null, 0), M("foss-", "overview-", 1));
        var d = FleetGovernor.Check(s, "overview-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.DuplicatePrefix, d.Reason);
    }

    [Fact]
    public void Rejects_unknown_parent()
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "ghost-", "foss-");
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.UnknownParent, d.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("no-trailing-dash")]
    [InlineData("Bad Prefix-")]
    public void Rejects_invalid_prefix(string prefix)
    {
        var s = State(12, 3, false, M("overview-", null, 0));
        var d = FleetGovernor.Check(s, "overview-", prefix);
        Assert.False(d.Allowed);
        Assert.Equal(RejectReason.InvalidPrefix, d.Reason);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "FleetGovernorTests" --nologo`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement the types**

Create `src/Styloagent.Core/Mcp/FleetTypes.cs`:

```csharp
namespace Styloagent.Core.Mcp;

public enum RejectReason { FleetFull, MaxDepth, Paused, DuplicatePrefix, InvalidPrefix, UnknownParent }

/// <summary>One live agent as the governor / list_fleet sees it.</summary>
public sealed record FleetMember(string Prefix, string Responsibility, string? ParentPrefix, int Depth, string State);

/// <summary>The fleet + its policy, handed to the pure governor.</summary>
public sealed record FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>What list_fleet returns to an agent.</summary>
public sealed record FleetSnapshot(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>A spawn_agent request, parented by prefix.</summary>
public sealed record SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt);

/// <summary>Result of a spawn attempt (never an exception).</summary>
public sealed record SpawnOutcome(bool Spawned, string? Prefix, RejectReason? Reason, string Message)
{
    public static SpawnOutcome Ok(string prefix) => new(true, prefix, null, $"spawned {prefix}");
    public static SpawnOutcome Reject(RejectReason reason, string message) => new(false, null, reason, message);
}

/// <summary>Governor verdict.</summary>
public sealed record Decision(bool Allowed, RejectReason? Reason, string Message)
{
    public static Decision Allow() => new(true, null, "allowed");
    public static Decision Deny(RejectReason reason, string message) => new(false, reason, message);
}
```

Create `src/Styloagent.Core/Mcp/IFleetController.cs`:

```csharp
namespace Styloagent.Core.Mcp;

/// <summary>
/// The seam the MCP tools call. Implemented in the App (marshals to the UI thread and drives the
/// roster); faked in tests. Keeps the tool layer app-agnostic.
/// </summary>
public interface IFleetController
{
    Task<SpawnOutcome> SpawnAsync(SpawnRequest req);
    FleetSnapshot Snapshot();
}
```

Create `src/Styloagent.Core/Mcp/FleetGovernor.cs`:

```csharp
namespace Styloagent.Core.Mcp;

/// <summary>Pure fleet policy: decides whether a spawn is allowed. No I/O, no state.</summary>
public static class FleetGovernor
{
    public static Decision Check(FleetState state, string parentPrefix, string newPrefix)
    {
        if (state.Paused)
            return Decision.Deny(RejectReason.Paused, "fleet is paused");

        if (!IsValidPrefix(newPrefix))
            return Decision.Deny(RejectReason.InvalidPrefix,
                $"'{newPrefix}' is not a valid prefix (lowercase word ending in '-')");

        var parent = state.Members.FirstOrDefault(m => m.Prefix == parentPrefix);
        if (parent is null)
            return Decision.Deny(RejectReason.UnknownParent, $"unknown parent '{parentPrefix}'");

        if (state.Members.Any(m => m.Prefix == newPrefix))
            return Decision.Deny(RejectReason.DuplicatePrefix, $"'{newPrefix}' already exists");

        if (state.Members.Count >= state.MaxFleet)
            return Decision.Deny(RejectReason.FleetFull, $"fleet full ({state.Members.Count}/{state.MaxFleet})");

        int childDepth = parent.Depth + 1;
        if (childDepth > state.MaxDepth)
            return Decision.Deny(RejectReason.MaxDepth, $"max depth {state.MaxDepth} reached");

        return Decision.Allow();
    }

    // A prefix is a lowercase token of [a-z0-9-] ending in '-' (e.g. "foss-").
    private static bool IsValidPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.EndsWith('-') || prefix.Length < 2)
            return false;
        foreach (char c in prefix)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        return true;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "FleetGovernorTests" --nologo`
Expected: PASS (all cases). Then `dotnet build src/Styloagent.Core --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Mcp/ tests/Styloagent.Core.Tests/FleetGovernorTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): fleet types + pure FleetGovernor + IFleetController seam

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: FleetPolicy + reader + scaffold fleet.yaml + system prompt

**Files:**
- Create: `src/Styloagent.Core/Projects/FleetPolicy.cs`
- Modify: `src/Styloagent.Core/Projects/ProjectConfig.cs`, `src/Styloagent.Core/Projects/ProjectScaffolder.cs`, `src/Styloagent.Core/Projects/DefaultTemplates.cs`
- Test: `tests/Styloagent.Core.Tests/FleetPolicyReaderTests.cs`

**Interfaces:**
- Consumes: existing `ProjectConfig` record + `ProjectConfig.For(string root)`, `ProjectScaffolder.Ensure(string root)`, `DefaultTemplates.SystemPrompt`.
- Produces: `sealed record FleetPolicy(int MaxFleet, int MaxDepth)` with `static FleetPolicy Default => new(12, 3)`; `static class FleetPolicyReader { FleetPolicy Read(string path); }` (tolerant, defaults on missing/invalid, never throws); `ProjectConfig.FleetPolicyPath` (`<ConfigDir>/fleet.yaml`).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/FleetPolicyReaderTests.cs`:

```csharp
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class FleetPolicyReaderTests
{
    [Fact]
    public void Reads_a_valid_policy()
    {
        var path = Path.Combine(Path.GetTempPath(), "fleet-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, "maxFleet: 6\nmaxDepth: 2\n");
        try
        {
            var p = FleetPolicyReader.Read(path);
            Assert.Equal(6, p.MaxFleet);
            Assert.Equal(2, p.MaxDepth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Returns_defaults_for_missing_or_invalid()
    {
        var missing = FleetPolicyReader.Read(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(FleetPolicy.Default, missing);

        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(bad, "this: [is not: valid");
        try { Assert.Equal(FleetPolicy.Default, FleetPolicyReader.Read(bad)); }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void Scaffolder_writes_fleet_yaml_and_does_not_overwrite_edits()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = ProjectScaffolder.Ensure(root);
            Assert.True(File.Exists(cfg.FleetPolicyPath));

            File.WriteAllText(cfg.FleetPolicyPath, "maxFleet: 3\nmaxDepth: 1\n");
            ProjectScaffolder.Ensure(root);   // second call must not clobber
            Assert.Equal(3, FleetPolicyReader.Read(cfg.FleetPolicyPath).MaxFleet);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "FleetPolicyReaderTests" --nologo`
Expected: FAIL — `FleetPolicy`/`FleetPolicyPath` don't exist.

- [ ] **Step 3: Implement**

Create `src/Styloagent.Core/Projects/FleetPolicy.cs`:

```csharp
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>Fleet guardrail limits (read from .styloagent/fleet.yaml).</summary>
public sealed record FleetPolicy(int MaxFleet, int MaxDepth)
{
    public static FleetPolicy Default => new(12, 3);
}

[YamlObject]
internal partial class FleetPolicyFile
{
    public int MaxFleet { get; set; } = 12;
    public int MaxDepth { get; set; } = 3;
}

/// <summary>Tolerant reader: defaults on missing/invalid, never throws.</summary>
public static class FleetPolicyReader
{
    public static FleetPolicy Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return FleetPolicy.Default;
            var bytes = File.ReadAllBytes(path);
            var file = YamlSerializer.Deserialize<FleetPolicyFile>(bytes);
            int maxFleet = file.MaxFleet > 0 ? file.MaxFleet : 12;
            int maxDepth = file.MaxDepth > 0 ? file.MaxDepth : 3;
            return new FleetPolicy(maxFleet, maxDepth);
        }
        catch { return FleetPolicy.Default; }
    }
}
```

In `src/Styloagent.Core/Projects/ProjectConfig.cs`, add `FleetPolicyPath` to the record and to `For`. Read the current file first; add the parameter after `LaunchPromptsDir` and set it in `For` to `Path.Combine(configDir, "fleet.yaml")` (match the existing naming of `configDir`/`ConfigDir` in that file).

In `src/Styloagent.Core/Projects/ProjectScaffolder.cs` `Ensure`, after the existing "write default if absent" blocks, add:

```csharp
        if (!File.Exists(config.FleetPolicyPath))
            File.WriteAllText(config.FleetPolicyPath, "maxFleet: 12\nmaxDepth: 3\n");
```

In `src/Styloagent.Core/Projects/DefaultTemplates.cs`, extend the `SystemPrompt` constant with a section teaching the tools (append inside the existing prompt string):

```
## Assembling your team

You have two MCP tools from the `styloagent` server:

- `list_fleet()` — returns the current fleet (prefix, responsibility, parent, depth, state).
  ALWAYS call this before spawning, to avoid creating a subsystem that already exists.
- `spawn_agent(prefix, responsibility, dir, launchPrompt)` — launches a child agent under you.
  `prefix` is a short lowercase tag ending in '-' (e.g. `foss-`). Give it a crisp single
  responsibility and a `launchPrompt` that tells it its job and to split further if warranted.

Decide the initial 3-4 subsystems, spawn them, and let them split. A spawn may be rejected
(`fleet full`, `max depth`, `paused`) — if so, stop spawning and coordinate via the channel
instead; do not retry blindly.
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "FleetPolicyReaderTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.Core --nologo` → fix any `error CA####`. Also run the existing scaffolder/project tests: `dotnet test tests/Styloagent.Core.Tests --nologo` — all green.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Projects/ tests/Styloagent.Core.Tests/FleetPolicyReaderTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): fleet.yaml policy + scaffold + system-prompt tool guidance

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: MCP tool type (spawn_agent + list_fleet) with header identity

**Files:**
- Create: `src/Styloagent.App/Mcp/FleetTools.cs`
- Modify: `src/Styloagent.App/Styloagent.App.csproj` (add `ModelContextProtocol.AspNetCore` package)
- Test: `tests/Styloagent.App.Tests/FleetToolsTests.cs`

**Interfaces:**
- Consumes: `IFleetController`, `SpawnRequest`, `SpawnOutcome`, `FleetSnapshot` (Task 1).
- Produces: `[McpServerToolType] class FleetTools(IHttpContextAccessor http, IFleetController controller, McpAuth auth)` with `Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt)` and `string list_fleet()`; `sealed class McpAuth(string Token)` used to validate the bearer + extract the caller prefix. Header constants `McpAuth.AgentHeader = "X-Styloagent-Agent"`.

**Note on package:** run `dotnet add src/Styloagent.App package ModelContextProtocol.AspNetCore --prerelease` (this brings `ModelContextProtocol.Server` attributes + the AspNetCore hosting). Confirm it restores; record the resolved version in the report.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/FleetToolsTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetToolsTests
{
    private sealed class FakeController : IFleetController
    {
        public SpawnRequest? LastReq;
        public SpawnOutcome Next = SpawnOutcome.Ok("foss-");
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) { LastReq = req; return Task.FromResult(Next); }
        public FleetSnapshot Snapshot() => new(
            new[] { new FleetMember("overview-", "the top", null, 0, "running") }, 12, 3, false);
    }

    private static IHttpContextAccessor AccessorWith(string? agent, string? auth)
    {
        var ctx = new DefaultHttpContext();
        if (agent is not null) ctx.Request.Headers[McpAuth.AgentHeader] = agent;
        if (auth is not null) ctx.Request.Headers["Authorization"] = auth;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public async Task spawn_agent_parents_by_header_and_returns_ok()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.spawn_agent("foss-", "owns FOSS", ".", "You are foss-.");

        Assert.Equal("overview-", ctrl.LastReq!.ParentPrefix);
        Assert.Equal("foss-", ctrl.LastReq.Prefix);
        Assert.Contains("spawned foss-", result);
    }

    [Fact]
    public async Task spawn_agent_reports_a_rejection()
    {
        var ctrl = new FakeController { Next = SpawnOutcome.Reject(RejectReason.FleetFull, "fleet full (12/12)") };
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.spawn_agent("foss-", "r", ".", "p");
        Assert.Contains("rejected", result);
        Assert.Contains("fleet full", result);
    }

    [Fact]
    public async Task spawn_agent_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.spawn_agent("foss-", "r", ".", "p");
        Assert.Null(ctrl.LastReq);            // never reached the controller
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public void list_fleet_serializes_the_snapshot()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.list_fleet();
        Assert.Contains("overview-", json);
        Assert.Contains("\"maxFleet\"", json);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetToolsTests" --nologo`
Expected: FAIL — `FleetTools`/`McpAuth` don't exist (add the package first so `[McpServerTool]` resolves).

- [ ] **Step 3: Implement**

Create `src/Styloagent.App/Mcp/FleetTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>Validates the per-run bearer token and extracts the caller prefix from the request.</summary>
public sealed class McpAuth
{
    public const string AgentHeader = "X-Styloagent-Agent";
    private readonly string _token;
    public McpAuth(string token) => _token = token;

    public bool TokenOk(HttpContext ctx)
        => ctx.Request.Headers.Authorization.ToString() == $"Bearer {_token}";

    public string? CallerPrefix(HttpContext ctx)
    {
        var v = ctx.Request.Headers[AgentHeader].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>The MCP tools agents call: spawn_agent + list_fleet.</summary>
[McpServerToolType]
public sealed class FleetTools
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHttpContextAccessor _http;
    private readonly IFleetController _controller;
    private readonly McpAuth _auth;

    public FleetTools(IHttpContextAccessor http, IFleetController controller, McpAuth auth)
        => (_http, _controller, _auth) = (http, controller, auth);

    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = _auth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt));

        return outcome.Spawned ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Return the current fleet: each agent's prefix, responsibility, parent, depth and state.")]
    public string list_fleet()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        return JsonSerializer.Serialize(_controller.Snapshot(), Json);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetToolsTests" --nologo`
Expected: PASS. Then `dotnet build src/Styloagent.App --nologo` → fix any `error CA####` (e.g. member ordering, static readonly).

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Mcp/FleetTools.cs src/Styloagent.App/Styloagent.App.csproj tests/Styloagent.App.Tests/FleetToolsTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): FleetTools (spawn_agent + list_fleet) with header identity + token

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: StyloagentMcpServer host + mcp-config builder

**Files:**
- Create: `src/Styloagent.App/Mcp/McpConfig.cs`, `src/Styloagent.App/Mcp/StyloagentMcpServer.cs`
- Test: `tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs`

**Interfaces:**
- Consumes: `FleetTools`, `McpAuth`, `IFleetController` (Task 3).
- Produces: `static class McpConfig { string BuildJson(string prefix, Uri url, string token); IReadOnlyList<string> Args(string prefix, Uri url, string token); }` (Args returns `["--mcp-config", <json>]`); `sealed class StyloagentMcpServer : IAsyncDisposable` with `static Task<StyloagentMcpServer> StartAsync(IFleetController controller)`, `Uri BaseUrl`, `string Token`, `bool IsRunning`, `IReadOnlyList<string> McpConfigArgs(string prefix)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Styloagent.App.Mcp;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class StyloagentMcpServerTests
{
    private sealed class FakeController : IFleetController
    {
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) => Task.FromResult(SpawnOutcome.Ok(req.Prefix));
        public FleetSnapshot Snapshot() => new(Array.Empty<FleetMember>(), 12, 3, false);
    }

    [Fact]
    public void McpConfig_json_names_the_server_url_prefix_and_token()
    {
        var json = McpConfig.BuildJson("foss-", new Uri("http://127.0.0.1:5000/mcp"), "tok");
        Assert.Contains("\"type\": \"http\"", json);
        Assert.Contains("127.0.0.1:5000/mcp", json);
        Assert.Contains("foss-", json);
        Assert.Contains("Bearer tok", json);
        var args = McpConfig.Args("foss-", new Uri("http://127.0.0.1:5000/mcp"), "tok");
        Assert.Equal("--mcp-config", args[0]);
    }

    [Fact]
    public async Task Server_starts_on_loopback_and_lists_the_two_tools()
    {
        await using var server = await StyloagentMcpServer.StartAsync(new FakeController());
        Assert.True(server.IsRunning);
        Assert.Equal("127.0.0.1", server.BaseUrl.Host);

        // MCP tools/list over the streamable-HTTP endpoint.
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { }
            })
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {server.Token}");
        req.Headers.TryAddWithoutValidation(McpAuth.AgentHeader, "overview-");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("spawn_agent", body);
        Assert.Contains("list_fleet", body);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "StyloagentMcpServerTests" --nologo`
Expected: FAIL — server/config types don't exist.

- [ ] **Step 3: Implement**

Create `src/Styloagent.App/Mcp/McpConfig.cs`:

```csharp
using System.Text.Json;

namespace Styloagent.App.Mcp;

/// <summary>Builds the --mcp-config a launched `claude` uses to reach our server.</summary>
public static class McpConfig
{
    public static string BuildJson(string prefix, Uri url, string token)
    {
        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["styloagent"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = url.ToString(),
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["X-Styloagent-Agent"] = prefix,
                        ["Authorization"] = $"Bearer {token}",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyList<string> Args(string prefix, Uri url, string token)
        => new[] { "--mcp-config", BuildJson(prefix, url, token) };
}
```

Create `src/Styloagent.App/Mcp/StyloagentMcpServer.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>
/// In-process HTTP MCP server (loopback, ephemeral port) exposing FleetTools. Each launched agent
/// gets a --mcp-config pointing here via <see cref="McpConfigArgs"/>.
/// </summary>
public sealed class StyloagentMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri BaseUrl { get; }
    public string Token { get; }
    public bool IsRunning { get; private set; }

    private StyloagentMcpServer(WebApplication app, Uri baseUrl, string token)
        => (_app, BaseUrl, Token) = (app, baseUrl, token);

    public static async Task<StyloagentMcpServer> StartAsync(IFleetController controller)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");   // ephemeral loopback port
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(controller);
        builder.Services.AddSingleton(new McpAuth(token));
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<FleetTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var addr = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var baseUrl = new Uri(new Uri(addr), "/mcp");
        return new StyloagentMcpServer(app, baseUrl, token) { IsRunning = true };
    }

    public IReadOnlyList<string> McpConfigArgs(string prefix) => McpConfig.Args(prefix, BaseUrl, Token);

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

If `WithTools<FleetTools>()` requires a public parameterless-friendly resolution, the DI-registered `IHttpContextAccessor`/`IFleetController`/`McpAuth` satisfy the ctor — no extra registration needed. If `CreateSlimBuilder` lacks routing for `MapMcp`, use `WebApplication.CreateBuilder` instead (note the choice in the report).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "StyloagentMcpServerTests" --nologo`
Expected: PASS (server starts on 127.0.0.1, tools/list returns both tools). Then `dotnet build src/Styloagent.App --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Mcp/McpConfig.cs src/Styloagent.App/Mcp/StyloagentMcpServer.cs tests/Styloagent.App.Tests/StyloagentMcpServerTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): in-process HTTP MCP server (loopback, ephemeral port) + mcp-config

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Parent/depth tracking + SpawnChild + FleetController

**Files:**
- Modify: `src/Styloagent.App/ViewModels/AgentPaneViewModel.cs`, `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`
- Create: `src/Styloagent.App/Mcp/FleetController.cs`
- Test: `tests/Styloagent.App.Tests/FleetSpawnTests.cs`

**Interfaces:**
- Consumes: `FleetGovernor`, `FleetState`, `FleetMember`, `FleetSnapshot`, `SpawnRequest`, `SpawnOutcome`, `IFleetController` (Task 1); `FleetPolicy` (Task 2); the existing `SpawnProposed`/`AddAgent` pane path.
- Produces on `MainWindowViewModel`: `FleetPolicy FleetPolicy { get; set; }` (default `FleetPolicy.Default`); `bool FleetPaused` (`[ObservableProperty]`); `[RelayCommand] void PauseFleet()` (toggles); `SpawnOutcome SpawnChild(SpawnRequest req)`; `FleetSnapshot BuildFleetSnapshot()`; `int FleetCount => Panes.Count`. On `AgentPaneViewModel`: `string? ParentPrefix`, `int Depth`, `string Responsibility` (set at construction; overview/manual = depth 0, parent null). `FleetController(MainWindowViewModel vm)` implementing `IFleetController` (marshals to UI thread).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/FleetSpawnTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetSpawnTests
{
    [Fact]
    public async Task SpawnChild_adds_a_parented_pane_at_depth_one()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();  // reuse existing helper
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            // Attach a project so child launch prompts have somewhere to go.
            var proj = Path.Combine(Path.GetTempPath(), "fleetproj-" + Guid.NewGuid().ToString("N"));
            vm.AttachProject(ProjectScaffolder.Ensure(proj));
            try
            {
                var overviewPrefix = vm.Panes[0].Prefix;   // first live agent acts as parent
                int before = vm.Panes.Count;

                var outcome = vm.SpawnChild(new SpawnRequest(overviewPrefix, "newsub-", "owns X", ".", "You are newsub-."));

                Assert.True(outcome.Spawned);
                Assert.Equal(before + 1, vm.Panes.Count);
                var child = vm.Panes.First(p => p.Prefix == "newsub-");
                Assert.Equal(overviewPrefix, child.ParentPrefix);
                Assert.Equal(vm.Panes[0].Depth + 1, child.Depth);
            }
            finally { if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SpawnChild_is_rejected_when_paused()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.PauseFleetCommand.Execute(null);
            var outcome = vm.SpawnChild(new SpawnRequest(vm.Panes[0].Prefix, "x-", "r", ".", "p"));
            Assert.False(outcome.Spawned);
            Assert.Equal(RejectReason.Paused, outcome.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BuildFleetSnapshot_reflects_the_roster()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var snap = vm.BuildFleetSnapshot();
            Assert.Equal(vm.Panes.Count, snap.Members.Count);
            Assert.Equal(12, snap.MaxFleet);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

(If `MakeTwoAgentChannel`/`FakeLauncher`/`FakeWatcher` are private in `MainWindowViewModelTests`, expose the helper as `internal static` or replicate the minimal channel-dir setup used there — match what that test file already does.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetSpawnTests" --nologo`
Expected: FAIL — `SpawnChild`/`ParentPrefix`/`PauseFleetCommand` don't exist.

- [ ] **Step 3: Implement**

On `AgentPaneViewModel`, add `public string? ParentPrefix { get; init; }`, `public int Depth { get; init; }`, `public string Responsibility { get; init; } = "";` (set via object-initializer where panes are created; existing creations default to null/0/""). **Also ensure `AgentPaneViewModel` exposes `public string Prefix`** — `BuildFleetSnapshot` and the tests read `pane.Prefix`. If the pane doesn't already surface the manifest prefix, add a `public string Prefix { get; }` set from the entry in the constructor (read the real file; the entry passed in has `.Prefix`).

On `MainWindowViewModel`:

```csharp
    [ObservableProperty] private bool _fleetPaused;
    public FleetPolicy FleetPolicy { get; set; } = FleetPolicy.Default;
    public int FleetCount => Panes.Count;

    [RelayCommand] private void PauseFleet() => FleetPaused = !FleetPaused;

    public FleetSnapshot BuildFleetSnapshot()
    {
        var members = Panes.Select(p => new FleetMember(
            p.Prefix, p.Responsibility, p.ParentPrefix, p.Depth,
            p.HookStateText ?? "running")).ToList();
        return new FleetSnapshot(members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
    }

    /// <summary>Governor-checked spawn from a parent prefix. Mirrors SpawnProposed but records lineage.</summary>
    public SpawnOutcome SpawnChild(SpawnRequest req)
    {
        var state = new FleetState(BuildFleetSnapshot().Members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
        var decision = FleetGovernor.Check(state, req.ParentPrefix, req.Prefix);
        if (!decision.Allowed) return SpawnOutcome.Reject(decision.Reason!.Value, decision.Message);

        int parentDepth = Panes.First(p => p.Prefix == req.ParentPrefix).Depth;
        var proposed = new ProposedAgent(req.Prefix, req.Responsibility, req.Dir, req.LaunchPrompt);
        // Reuse the existing pane-creation path, then stamp lineage onto the new pane.
        var paneVm = CreatePaneForProposed(proposed, parentPrefix: req.ParentPrefix, depth: parentDepth + 1);
        return paneVm is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(req.Prefix);
    }
```

Refactor the body of the existing `SpawnProposed(ProposedAgent p)` into a private
`AgentPaneViewModel? CreatePaneForProposed(ProposedAgent p, string? parentPrefix = null, int depth = 0)`
that builds the entry, reserves the hook id, creates the `AgentPaneViewModel` (now passing
`ParentPrefix`/`Depth`/`Responsibility = p.Responsibility` via the initializer), adds it to `Panes`
+ dock, spawns it, and returns the pane (or null on guard failure). Have `SpawnProposed` call
`CreatePaneForProposed(p)` and still remove the proposal. `SpawnChild` calls it with lineage. This
keeps one pane-creation path (DRY).

Create `src/Styloagent.App/Mcp/FleetController.cs`:

```csharp
using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>Bridges the MCP tools to the cockpit VM, marshalling every call to the UI thread.</summary>
public sealed class FleetController : IFleetController
{
    private readonly MainWindowViewModel _vm;
    public FleetController(MainWindowViewModel vm) => _vm = vm;

    public Task<SpawnOutcome> SpawnAsync(SpawnRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.SpawnChild(req)).GetTask();

    public FleetSnapshot Snapshot()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildFleetSnapshot()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildFleetSnapshot).GetTask().GetAwaiter().GetResult();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetSpawnTests" --nologo`
Expected: PASS. Then `dotnet test tests/Styloagent.App.Tests --nologo` (all existing App tests still green — the `SpawnProposed` refactor must not regress the bootstrap tests). Then `dotnet build src/Styloagent.App --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/AgentPaneViewModel.cs src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/Mcp/FleetController.cs tests/Styloagent.App.Tests/FleetSpawnTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): SpawnChild with parent/depth lineage + governor + FleetController

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Startup wiring — server on launch, mcp-config on every agent, policy load, degrade

**Files:**
- Modify: `src/Styloagent.App/ViewModels/MainWindowViewModel.cs`, `src/Styloagent.App/App.axaml.cs`
- Test: `tests/Styloagent.App.Tests/FleetWiringTests.cs`

**Interfaces:**
- Consumes: `StyloagentMcpServer` (Task 4), `FleetController` (Task 5), `FleetPolicyReader`/`FleetPolicy` + `ProjectConfig.FleetPolicyPath` (Task 2), the existing `AttachProject`/`InitializeAsync` overview path (bootstrap).
- Produces on `MainWindowViewModel`: `Task StartFleetServerAsync()` (starts the server + creates a `FleetController(this)`, idempotent; stores `IReadOnlyList<string> McpArgsFor(string prefix)` returning `server.McpConfigArgs(prefix)` or empty when the server didn't start); `bool McpServerRunning`; `string? McpServerWarning`. `AttachProject` also loads `FleetPolicy = FleetPolicyReader.Read(project.FleetPolicyPath)`. Overview + every child launch appends `McpArgsFor(prefix)` to their `AgentSession` args.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.App.Tests/FleetWiringTests.cs`:

```csharp
using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetWiringTests
{
    [Fact]
    public async Task StartFleetServer_runs_and_overview_launch_carries_mcp_config()
    {
        var proj = Path.Combine(Path.GetTempPath(), "wire-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        var launcher = new CapturingLauncher();  // records PtySpawnOptions.Args (see MainWindowViewModelTests fakes)
        try
        {
            var promptPath = cfg.SystemPromptPath;   // exists from scaffold
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, launcher, new FakeWatcher(),
                repoRoot: cfg.Root, overviewSystemPromptPath: promptPath);
            vm.AttachProject(cfg);
            await vm.StartFleetServerAsync();

            Assert.True(vm.McpServerRunning);
            // The overview's spawn args should include a --mcp-config once the server is up and the
            // overview is (re)launched through the fleet path. Assert the plumbing is present:
            Assert.NotEmpty(vm.McpArgsFor("overview-"));
            Assert.Equal("--mcp-config", vm.McpArgsFor("overview-")[0]);
        }
        finally { if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true); }
    }

    [Fact]
    public async Task AttachProject_loads_the_fleet_policy()
    {
        var proj = Path.Combine(Path.GetTempPath(), "pol-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        File.WriteAllText(cfg.FleetPolicyPath, "maxFleet: 5\nmaxDepth: 2\n");
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher());
            vm.AttachProject(cfg);
            Assert.Equal(5, vm.FleetPolicy.MaxFleet);
            Assert.Equal(2, vm.FleetPolicy.MaxDepth);
        }
        finally { if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true); }
    }
}
```

(Add a `CapturingLauncher : IPtyLauncher` to the test project if one doesn't exist — capture `PtySpawnOptions.Args` on `SpawnAsync`, return a `FakePtySession`. Reuse the existing `FakeLauncher`/`FakeWatcher`/`FakePtySession` from `MainWindowViewModelTests`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetWiringTests" --nologo`
Expected: FAIL — `StartFleetServerAsync`/`McpArgsFor`/`McpServerRunning` don't exist.

- [ ] **Step 3: Implement**

On `MainWindowViewModel`, add fields + methods:

```csharp
    private StyloagentMcpServer? _mcpServer;
    public bool McpServerRunning => _mcpServer?.IsRunning ?? false;
    public string? McpServerWarning { get; private set; }

    /// <summary>Starts the in-process MCP server + fleet controller. Idempotent; degrades on failure.</summary>
    public async Task StartFleetServerAsync()
    {
        if (_mcpServer is not null) return;
        try
        {
            _mcpServer = await StyloagentMcpServer.StartAsync(new FleetController(this));
        }
        catch (Exception ex)
        {
            McpServerWarning = $"MCP server unavailable — agents cannot spawn subteams: {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"[Styloagent] {McpServerWarning}");
        }
    }

    /// <summary>The --mcp-config args for a given agent prefix (empty when the server isn't running).</summary>
    public IReadOnlyList<string> McpArgsFor(string prefix)
        => _mcpServer is { IsRunning: true } s ? s.McpConfigArgs(prefix) : Array.Empty<string>();
```

Thread `McpArgsFor(prefix)` into every launch:
- In `InitializeAsync`'s overview branch, where the first pane's `AgentSession` args are built, add `.Concat(vm.McpArgsFor("overview-"))`. Because the server may start after InitializeAsync, ALSO ensure `StartFleetServerAsync` is awaited in the bootstrap startup BEFORE the overview session spawns (App.axaml.cs, below) — so the args are present at spawn time.
- In `CreatePaneForProposed` (Task 5), append `McpArgsFor(p.Prefix)` to the child's `AgentSession` args alongside `HookArgs(hookId)`.

In `AttachProject`, add: `FleetPolicy = FleetPolicyReader.Read(project.FleetPolicyPath);`

In `src/Styloagent.App/App.axaml.cs` `OpenProjectAsync`, after building the cockpit VM and before/around showing it, start the fleet server so overview launches with mcp-config:

```csharp
                var vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new PortaPtyLauncher(), new FileSystemFileWatcher(),
                    repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath);
                vm.AttachProject(cfg);
                await vm.StartFleetServerAsync();   // <-- server up before the cockpit shows
```

(Read the real `App.axaml.cs` — this must sit inside the existing try/catch and keep the existing dispatcher/show/close-welcome flow. Match the real launcher/watcher type names.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.App.Tests --filter "FleetWiringTests" --nologo`
Expected: PASS. Then `dotnet test --nologo` (whole solution green). Then `dotnet build --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/MainWindowViewModel.cs src/Styloagent.App/App.axaml.cs tests/Styloagent.App.Tests/FleetWiringTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): start fleet server on launch + mcp-config on every agent + policy load

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Roster tree UI — depth indent + fleet/depth HUD + Pause toggle

**Files:**
- Modify: `src/Styloagent.App/Views/AgentsView.axaml`
- Test: `tests/Styloagent.UITests/FleetHudTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.FleetCount`/`FleetPolicy`/`FleetPaused`/`PauseFleetCommand` (Tasks 5-6); `AgentPaneViewModel.Depth` (Task 5); the existing `CountToBoolConverter`.
- Produces: a roster header HUD showing `fleet N/max · depth d/max` and a Pause toggle; roster rows indented by `Depth`.

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.UITests/FleetHudTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class FleetHudTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public FleetHudTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Roster_shows_fleet_hud_and_pause_toggle()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        return _fx.DispatchAsync(async () =>
        {
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 360, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s.Contains("fleet") && s.Contains("/"));   // HUD present
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => (b.Content?.ToString() ?? "").Contains("Pause"));
                window.Close();
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Styloagent.UITests --filter "FleetHudTests" --nologo`
Expected: FAIL — no HUD/Pause in the roster.

- [ ] **Step 3: Implement**

In `src/Styloagent.App/Views/AgentsView.axaml`, replace the header `Border` (the one with the "Agents" title) so it also carries the HUD + Pause, and indent rows by depth. Header:

```xml
      <Border Grid.Row="0" Background="#1A1A2E" Padding="8,6">
        <Grid ColumnDefinitions="*,Auto">
          <StackPanel>
            <TextBlock Text="Agents" FontWeight="Bold" FontSize="13" Foreground="#9D7FE0" LetterSpacing="1" />
            <TextBlock FontSize="10" Foreground="#7A7AA0">
              <Run Text="fleet " /><Run Text="{Binding FleetCount}" /><Run Text="/" /><Run Text="{Binding FleetPolicy.MaxFleet}" />
              <Run Text=" · depth " /><Run Text="{Binding FleetPolicy.MaxDepth}" /><Run Text=" max" />
            </TextBlock>
          </StackPanel>
          <ToggleButton Grid.Column="1" VerticalAlignment="Center" FontSize="10" Padding="6,2"
                        IsChecked="{Binding FleetPaused}" Command="{Binding PauseFleetCommand}"
                        Content="⏸ Pause" />
        </Grid>
      </Border>
```

Indent roster rows by depth: in the live-roster `ItemsControl`'s row template (`AgentRowTemplate`), bind the row `Border`'s left margin to `Depth` via a converter. Add a small `DepthToMarginConverter` in `src/Styloagent.App/Converters/DepthToMarginConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;

namespace Styloagent.App.Converters;

/// <summary>Indents a roster row by its depth (12px per level).</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public static readonly DepthToMarginConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Avalonia.Thickness(value is int d ? d * 12 : 0, 0, 0, 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Apply `Margin="{Binding Depth, Converter={x:Static conv:DepthToMarginConverter.Instance}}"` to the row's outer `Border` in `AgentRowTemplate` (the `AgentsView` already declares `xmlns:conv`). If `FleetPolicy` isn't reachable via binding (it's a plain property, not observable), expose `MaxFleet`/`MaxDepth` as direct int properties on the VM (`public int MaxFleet => FleetPolicy.MaxFleet;`) and bind those instead — note the choice in the report.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Styloagent.UITests --filter "FleetHudTests" --nologo`
Expected: PASS. Then `dotnet test --nologo` (whole solution green). Then `dotnet build --nologo` → fix any `error CA####`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/Views/AgentsView.axaml src/Styloagent.App/Converters/DepthToMarginConverter.cs tests/Styloagent.UITests/FleetHudTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(mcp): roster fleet/depth HUD + Pause toggle + depth-indented rows

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes / follow-ups (not this plan)

- **`despawn`/kill verb** — Pause is the only runtime control this slice; a per-agent stop is a fast-follow.
- **Per-agent git worktrees** — children reuse dir resolution; real isolation deferred.
- **README demo** — add a recursive-fleet screenshot (overview + children indented) once landed.
- **Plan-time verification captured in Task 3/4** — record the resolved `ModelContextProtocol.AspNetCore` version and whether `WebApplication.CreateSlimBuilder` + `MapMcp` worked or `CreateBuilder` was needed.
