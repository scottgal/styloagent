using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.App.Tests;

public partial class StyloagentMcpServerTests
{
    private sealed class LiveOpenCodeFactAttribute : FactAttribute
    {
        public LiveOpenCodeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("STYLOAGENT_LIVE_OPENCODE") != "1")
                Skip = "Opt in with STYLOAGENT_LIVE_OPENCODE=1; uses installed OpenCode v2 and real DeepSeek credits.";
        }
    }

    private sealed class RecordingOpenCodeLauncher : IPtyLauncher
    {
        public PtySpawnOptions? Last { get; private set; }
        public IPtySession? Terminal { get; private set; }
        public async Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
        {
            Last = options;
            return Terminal = await new PortaPtyLauncher().SpawnAsync(options, ct);
        }
        public HttpClient Client(OpenCodeSession session)
        {
            var client = new HttpClient { BaseAddress = session.ServerUrl };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Last!.Env!["OPENCODE_SERVER_PASSWORD"])));
            return client;
        }
    }

    [LiveOpenCodeFact]
    public async Task Native_adapter_runs_real_MCP_delivery_permissions_terminal_detach_and_restore()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await InitializeGitAsync(root);
        var hooks = Path.Combine(root, "hooks");
        var inbox = new PendingInbox(hooks);
        await using var mcp = await Mcp.StyloagentMcpServer.StartAsync(new FakeController { NativeRepo = Path.GetFileName(root) }, new FakeRouter(), hooks);
        var options = new OpenCodeOptions("native-test-", root, "native-test-", hooks, mcp.BaseUrl,
            "This is a disposable integration test. Perform only the requested operation. Never delegate. Read bus message files with the read tool, never shell. Only run shell when the user explicitly requests a shell command. End replies with NATIVE_INSTRUCTIONS_OK.",
            StateDirectory: Path.Combine(root, "native"));
        var spawn = new PtySpawnOptions("opencode", [], root,
            new Dictionary<string, string> { ["STYLOAGENT_MCP_TOKEN"] = mcp.Token, ["TERM"] = "xterm-256color" }, 100, 30);
        var launcher = new RecordingOpenCodeLauncher();
        string sid;
        await using (var session = await OpenCodeSession.StartAsync(options, spawn, launcher,
            "Call the Styloagent list_fleet MCP tool to verify the fleet connection. Use Code Mode search if needed. Then reply WIRE_OK.", "deepseek/deepseek-flash", "none"))
        {
            sid = session.SessionId;
            using var client = launcher.Client(session);
            using var anonymous = new HttpClient { BaseAddress = session.ServerUrl };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/info")).StatusCode);
            Assert.Contains("--server", launcher.Last!.Args);
            Assert.Contains(sid, launcher.Last.Args);
            await UntilAsync(async () => await CompletedAsync(session, client, "WIRE_OK"));
            var first = await NativeMessagesAsync(client, sid);
            Assert.Contains("NATIVE_INSTRUCTIONS_OK", first.GetRawText());
            Assert.Contains("list_fleet", first.GetRawText());
            Assert.DoesNotContain("not a member", first.GetRawText());
            Assert.NotNull(session.Usage);
            Assert.Contains(OpenCodeModelCatalog.For(root), m => m.QualifiedId == "deepseek/deepseek-flash" && m.Variants.Contains("none"));
            await Assert.ThrowsAsync<IOException>(() => OpenCodeSession.StartAsync(options, spawn, launcher, "", null, null));

            var path = Path.Combine(root, "native-test-message.md");
            await File.WriteAllTextAsync(path, "Reply BUS_RECEIVED. No tools required.");
            var message = new BusMessage("message", "native-test-", BusMessageKind.Inbox, BusMessageState.New,
                path, DateTimeOffset.UtcNow, "operator-", "Reply BUS_RECEIVED. No tools required.");
            await session.DeliverAsync(message, DeliveryMode.NextPrompt);
            await session.DeliverAsync(message, DeliveryMode.NextPrompt);
            await UntilAsync(async () => { await session.ReconcileAsync(); return inbox.PickedUp("native-test-", path); });
            await UntilAsync(async () => await CompletedAsync(session, client, "BUS_RECEIVED"));
            var admitted = await NativeMessagesAsync(client, sid);
            Assert.Single(admitted.EnumerateArray(), m => m.GetProperty("type").GetString() == "user" && m.GetRawText().Contains(Path.GetFileName(path), StringComparison.Ordinal));

            await session.SendAsync("Run the shell command printf PERMISSION_EXECUTED. Then reply APPROVAL_DONE.", "msg_" + Guid.NewGuid().ToString("N"));
            await UntilAsync(async () => { await session.ReconcileAsync(); return session.PendingPermissionId is not null; });
            await session.ReplyToPermissionAsync("once");
            await UntilAsync(async () => await CompletedAsync(session, client, "APPROVAL_DONE"));
            Assert.Contains("PERMISSION_EXECUTED", (await NativeMessagesAsync(client, sid)).GetRawText());

            await session.SendAsync("Use shell to run sleep 30, then reply SLEEP_FINISHED. Do not use any other tool.", "msg_" + Guid.NewGuid().ToString("N"));
            await UntilAsync(async () => { await session.ReconcileAsync(); return session.PendingPermissionId is not null; });
            await session.ReplyToPermissionAsync("once");
            await UntilAsync(async () => (await NativeMessagesAsync(client, sid)).EnumerateArray()
                .Where(m => m.GetProperty("type").GetString() == "assistant")
                .SelectMany(m => m.GetProperty("content").EnumerateArray())
                .Any(p => p.GetProperty("type").GetString() == "tool" && p.GetProperty("name").GetString() == "shell" &&
                    p.GetProperty("state").GetProperty("status").GetString() == "running"));
            var urgentPath = Path.Combine(root, "native-test-urgent.md");
            await File.WriteAllTextAsync(urgentPath, "The previous shell task is cancelled. Reply URGENT_RECEIVED. No more shell commands.");
            var urgent = message with { Slug = "urgent", FilePath = urgentPath, Priority = MessagePriority.Urgent };
            var interruptedAt = System.Diagnostics.Stopwatch.StartNew();
            await session.DeliverAsync(urgent, DeliveryMode.Interrupt);
            await UntilAsync(async () => await CompletedAsync(session, client, "URGENT_RECEIVED"));
            Assert.True(interruptedAt.Elapsed < TimeSpan.FromSeconds(20), "Urgent delivery should interrupt the 30-second shell command.");
            Assert.True(inbox.PickedUp("native-test-", urgentPath));

            // TUI disposal must leave HTTP execution alive, and automated sends must still work.
            await launcher.Terminal!.DisposeAsync();
            Assert.True(session.ProcessId > 0);
            await session.SendAsync("Reply DETACHED_OK. No tools.", "msg_" + Guid.NewGuid().ToString("N"));
            await UntilAsync(async () => await CompletedAsync(session, client, "DETACHED_OK"));
        }
        // Simulate a process dying after the local receipt is durable but before POST admission.
        var recoveryPath = Path.Combine(root, "native-test-recovery.md");
        await File.WriteAllTextAsync(recoveryPath, "Reply RECOVERY_OK. No tools.");
        var recoveryId = "msg_" + Guid.NewGuid().ToString("N");
        var stateFile = Path.Combine(options.StateDirectory!, "session.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(stateFile))!;
        state["Pending"]![recoveryId] = recoveryPath;
        state["Inputs"]![recoveryId] = "Reply RECOVERY_OK. No tools.";
        await File.WriteAllTextAsync(stateFile, state.ToJsonString());
        await using (var restored = await OpenCodeSession.StartAsync(options, spawn, launcher, "", "deepseek/deepseek-flash", "low"))
        {
            using var client = launcher.Client(restored);
            Assert.Equal(sid, restored.SessionId);
            Assert.Equal("low", restored.Effort);
            await UntilAsync(async () => await CompletedAsync(restored, client, "RECOVERY_OK"));
            Assert.True(inbox.PickedUp("native-test-", recoveryPath));
            await restored.SendAsync("Reply RESTORED_OK. No tools.", "msg_" + Guid.NewGuid().ToString("N"));
            await UntilAsync(async () => await CompletedAsync(restored, client, "RESTORED_OK"));
            Assert.Contains("BUS_RECEIVED", (await NativeMessagesAsync(client, sid)).GetRawText());
        }
        Directory.Delete(root, true);
    }

    [LiveOpenCodeFact]
    public async Task Agent_session_parks_and_resumes_native_history_without_a_checkpoint_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-native-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await InitializeGitAsync(root);
        var launcher = new RecordingOpenCodeLauncher();
        var manifest = new AgentManifestEntry("native-test-", root, root, "", "", "", AgentTransport.Local,
            AgentRuntimeKind.OpenCode, Model: "deepseek/deepseek-flash", Effort: "none");
        var session = new AgentSession(manifest, launcher, new FakeWatcher { WillChange = false },
            openCodeOptions: new("native-test-", root, "native-test-", StateDirectory: Path.Combine(root, "native")));
        try
        {
            await session.SpawnAsync("Remember PARK_MARKER_77 and reply SESSION_STARTED. No tools.");
            var native = Assert.IsType<OpenCodeSession>(session.CurrentPty);
            var sid = native.SessionId;
            using (var client = launcher.Client(native))
            {
                await UntilAsync(async () => await CompletedAsync(native, client, "SESSION_STARTED"));
                var compact = await client.PostAsync($"/api/session/{sid}/compact", new StringContent("{}", Encoding.UTF8, "application/json"));
                compact.EnsureSuccessStatusCode();
                await UntilAsync(async () => (await NativeMessagesAsync(client, sid)).EnumerateArray()
                    .Any(m => m.GetProperty("type").GetString() == "compaction" && m.GetProperty("status").GetString() == "completed"));
            }
            Assert.True(await session.DehydrateAsync(TimeSpan.Zero));
            Assert.Equal(SessionState.Dehydrated, session.State);
            Assert.Null(session.CurrentPty);
            await session.RehydrateAsync("What marker did I ask you to remember? Reply with the marker only. No tools.");
            var resumed = Assert.IsType<OpenCodeSession>(session.CurrentPty);
            Assert.Equal(sid, resumed.SessionId);
            using var restoredClient = launcher.Client(resumed);
            await UntilAsync(async () => await CompletedAsync(resumed, restoredClient, "PARK_MARKER_77"));
            System.Diagnostics.Process.GetProcessById(resumed.ProcessId).Kill(entireProcessTree: true);
            await UntilAsync(() => Task.FromResult(session.State == SessionState.Exited));
            await session.SpawnAsync("Reply CRASH_RESTARTED. No tools.");
            var recovered = Assert.IsType<OpenCodeSession>(session.CurrentPty);
            Assert.Equal(sid, recovered.SessionId);
            using var recoveredClient = launcher.Client(recovered);
            await UntilAsync(async () => await CompletedAsync(recovered, recoveredClient, "CRASH_RESTARTED"));
        }
        finally { await session.KillAsync(); Directory.Delete(root, true); }
    }

    [LiveOpenCodeFact]
    public async Task Native_backends_write_only_to_their_respective_real_git_worktrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-native-worktrees-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        await InitializeGitAsync(repo);
        async Task Git(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "initial");
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        await Git("worktree", "add", "-b", "test-a", a);
        await Git("worktree", "add", "-b", "test-b", b);
        OpenCodeSession? first = null, second = null;
        try
        {
            var launcherA = new RecordingOpenCodeLauncher();
            var launcherB = new RecordingOpenCodeLauncher();
            first = await OpenCodeSession.StartAsync(new("a-", repo, "a-", StateDirectory: Path.Combine(root, "state-a")),
                new("opencode", [], a, null, 100, 30), launcherA,
                "Use write to create result.txt containing WORKTREE_A. Then reply WRITE_A_DONE. Do not use shell.", "deepseek/deepseek-flash", "none");
            second = await OpenCodeSession.StartAsync(new("b-", repo, "b-", StateDirectory: Path.Combine(root, "state-b")),
                new("opencode", [], b, null, 100, 30), launcherB,
                "Use write to create result.txt containing WORKTREE_B. Then reply WRITE_B_DONE. Do not use shell.", "deepseek/deepseek-flash", "none");
            using var clientA = launcherA.Client(first);
            using var clientB = launcherB.Client(second);
            await UntilAsync(async () => await CompletedAsync(first, clientA, "WRITE_A_DONE"));
            await UntilAsync(async () => await CompletedAsync(second, clientB, "WRITE_B_DONE"));
            Assert.Equal("WORKTREE_A", (await File.ReadAllTextAsync(Path.Combine(a, "result.txt"))).Trim());
            Assert.Equal("WORKTREE_B", (await File.ReadAllTextAsync(Path.Combine(b, "result.txt"))).Trim());
            Assert.False(File.Exists(Path.Combine(repo, "result.txt")));
            Assert.NotEqual(first.ProcessId, second.ProcessId);
        }
        finally
        {
            if (first is not null) await first.DisposeAsync();
            if (second is not null) await second.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [LiveOpenCodeFact]
    public async Task Native_ownership_plugin_blocks_a_real_write_before_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-native-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".styloagent"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await InitializeGitAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".styloagent", "ownership.yaml"), "owners:\n  other-:\n    - src/**\n");
        var owned = Path.Combine(root, "src", "Owned.txt");
        await File.WriteAllTextAsync(owned, "ORIGINAL");
        // Exercise the actual app's --owner-gate entry point, not a mock permission decision.
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Styloagent.App/bin/Debug/net10.0/Styloagent.App.dll"));
        Assert.True(File.Exists(app), app);
        var options = new OpenCodeOptions("worker-", root, "worker-", PermissionMode: FleetPermissionMode.Scoped,
            GateCommand: ["dotnet", app], StateDirectory: Path.Combine(root, "native"));
        var launcher = new RecordingOpenCodeLauncher();
        await using (var session = await OpenCodeSession.StartAsync(options, new("opencode", [], root, null, 100, 30), launcher,
            "Use the write tool once to replace src/Owned.txt with CHANGED. If permission is denied, stop and report OWNERSHIP_DENIED. Do not use shell or try another tool.",
            "deepseek/deepseek-flash", "none"))
        {
            using var client = launcher.Client(session);
            await UntilAsync(async () => await CompletedAsync(session, client, "OWNERSHIP_DENIED"));
            Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(owned));
            Assert.Contains("other-", (await NativeMessagesAsync(client, session.SessionId)).GetRawText());
        }
        Directory.Delete(root, true);
    }

    private static async Task InitializeGitAsync(string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "native/\nhooks/\n");
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init");
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<JsonElement> NativeMessagesAsync(HttpClient client, string sid)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/session/{sid}/message?order=desc&limit=100"));
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<bool> CompletedAsync(OpenCodeSession session, HttpClient client, string marker)
    {
        await session.ReconcileAsync();
        Assert.Null(session.PendingPermissionId);
        var messages = await NativeMessagesAsync(client, session.SessionId);
        return session.IsIdle && messages.EnumerateArray().Any(m => m.GetProperty("type").GetString() == "assistant" &&
            m.GetProperty("content").EnumerateArray().Any(p => p.GetProperty("type").GetString() == "text" && p.GetProperty("text").GetString()!.Contains(marker, StringComparison.Ordinal)));
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await predicate()) await Task.Delay(200, deadline.Token);
    }
}
