using System.Net;
using System.Text;
using Styloagent.Core.Hooks;
using Styloagent.Core.Sessions;

public class OpenCodeReconciliationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Null_errors_never_request_input(bool working)
    {
        var api = new SnapshotApi { Working = working, Messages = """[{"id":"a1","type":"assistant","error":null}]""" };
        await using var session = Create(api);
        var events = new List<HookEvent>();
        session.RuntimeEvent += events.Add;
        await session.ReconcileAsync();
        await session.ReconcileAsync();
        var value = Assert.Single(events);
        Assert.Equal(working ? "UserPromptSubmit" : "Notification", value.EventName);
        Assert.Equal(working ? null : "idle_prompt", value.NotificationType);
    }

    [Fact]
    public async Task Failed_turn_is_reported_once_and_clears_when_work_resumes()
    {
        var api = new SnapshotApi { Messages = """[{"id":"a1","type":"assistant","error":{"message":"failed"}}]""" };
        await using var session = Create(api);
        var events = new List<HookEvent>();
        session.RuntimeEvent += events.Add;
        await session.ReconcileAsync();
        await session.ReconcileAsync();
        Assert.Equal("agent_needs_input", Assert.Single(events).NotificationType);
        api.Working = true;
        await session.ReconcileAsync();
        await session.ReconcileAsync();
        Assert.Equal("UserPromptSubmit", events.Last().EventName);
        Assert.Equal(2, events.Count);
        // Even before a new assistant response exists, a newer user turn supersedes the error.
        api.Working = false;
        api.Messages = """[{"id":"u2","type":"user"},{"id":"a1","type":"assistant","error":{"message":"failed"}}]""";
        await session.ReconcileAsync();
        Assert.Equal("idle_prompt", events.Last().NotificationType);
        api.Messages = """[{"id":"a2","type":"assistant","error":null}]""";
        await session.ReconcileAsync();
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task Real_permissions_and_forms_still_request_input_and_clear()
    {
        var api = new SnapshotApi { Working = true, Permissions = """[{"id":"p1","action":"shell","resources":["echo test"]}]""" };
        await using var session = Create(api);
        var events = new List<HookEvent>();
        session.RuntimeEvent += events.Add;
        await session.ReconcileAsync();
        await session.ReconcileAsync();
        Assert.Equal("PermissionRequest", Assert.Single(events).EventName);
        api.Permissions = "[]";
        api.Forms = """[{"id":"f1","title":"Choose an option"}]""";
        await session.ReconcileAsync();
        Assert.Equal("agent_needs_input", events.Last().NotificationType);
        api.Forms = "[]";
        await session.ReconcileAsync();
        Assert.Equal("UserPromptSubmit", events.Last().EventName);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task Poll_failure_is_nonblocking_deduplicated_and_recovers()
    {
        var api = new SnapshotApi { Fail = true };
        await using var session = Create(api);
        var events = new List<HookEvent>();
        var state = AgentHookState.Working;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RuntimeEvent += e =>
        {
            events.Add(e);
            state = HookStateMachine.Next(state, e);
            if (e.NotificationType == "idle_prompt") recovered.TrySetResult();
        };
        var poll = session.PollAsync(deadline.Token);
        try
        {
            // Leave two consecutive polls failing, then let the next one recover.
            await api.SecondFailure.Task.WaitAsync(deadline.Token);
            Assert.Equal("connection_interrupted", Assert.Single(events).NotificationType);
            Assert.Equal(AgentHookState.Working, state);
            api.Fail = false;
            await recovered.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            await poll;
        }
        Assert.Equal(AgentHookState.Idle, state);
        Assert.DoesNotContain(events, e => e.NotificationType == "agent_needs_input");
    }

    private static OpenCodeSession Create(SnapshotApi api)
    {
        var root = Path.GetTempPath();
        return new(new OpenCodeOptions("test-", root, "test"),
            new PtySpawnOptions("unused", [], root, null, 80, 24), new UnusedLauncher(),
            new HttpClient(api) { BaseAddress = new Uri("http://localhost") });
    }

    private sealed class UnusedLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class SnapshotApi : HttpMessageHandler
    {
        public bool Working;
        public volatile bool Fail;
        public string Messages = "[]";
        public string Permissions = "[]";
        public string Forms = "[]";
        private int _failures;
        public TaskCompletionSource SecondFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail)
            {
                if (Interlocked.Increment(ref _failures) == 2) SecondFailure.TrySetResult();
                throw new HttpRequestException("temporary disconnection");
            }
            var data = request.RequestUri!.AbsolutePath switch
            {
                "/api/model" => "[]",
                "/api/session/active" => Working ? """{"":{"status":"busy"}}""" : "{}",
                var path when path.EndsWith("/permission") => Permissions,
                var path when path.EndsWith("/form") => Forms,
                var path when path.EndsWith("/message") => Messages,
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":" + data + "}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
