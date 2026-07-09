using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.Core.Tests;

public class HookChannelTests : IAsyncLifetime
{
    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "styloagent-hooks-test", Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return Task.CompletedTask;
    }

    private void Drop(string agentId, string json)
    {
        // Mirrors the real hook command's output: <agentId>__<uuid>.json
        string name = $"{agentId}__{Guid.NewGuid():N}.json";
        File.WriteAllText(Path.Combine(_dir, name), json);
    }

    [Fact]
    public async Task ScanOnce_routes_a_dropped_event_and_consumes_the_file()
    {
        await using var channel = new HookChannel(_dir);
        var received = new List<HookEvent>();
        channel.EventReceived += received.Add;

        Drop("web", """{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Allow?"}""");
        channel.ScanOnce();

        Assert.Single(received);
        Assert.Equal("web", received[0].AgentId);
        Assert.Equal("Notification", received[0].EventName);
        Assert.Equal("permission_prompt", received[0].NotificationType);
        // File is consumed so it isn't re-routed on the next scan.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ScanOnce_ignores_files_with_unrecognized_names()
    {
        await using var channel = new HookChannel(_dir);
        var received = new List<HookEvent>();
        channel.EventReceived += received.Add;

        File.WriteAllText(Path.Combine(_dir, "notanagentfile.json"), """{"hook_event_name":"Stop"}""");
        channel.ScanOnce();

        Assert.Empty(received);
    }

    [Fact]
    public async Task Polling_loop_picks_up_a_file_written_after_start()
    {
        await using var channel = new HookChannel(_dir, TimeSpan.FromMilliseconds(25));
        var tcs = new TaskCompletionSource<HookEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.EventReceived += e => tcs.TrySetResult(e);
        channel.Start();

        Drop("api", """{"hook_event_name":"PreToolUse","tool_name":"Bash"}""");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.True(completed == tcs.Task, "polling loop should pick up the dropped file within 3s");
        Assert.Equal("api", tcs.Task.Result.AgentId);
        Assert.Equal("PreToolUse", tcs.Task.Result.EventName);
    }

    [Fact]
    public async Task SettingsArgsFor_targets_this_channels_directory()
    {
        await using var channel = new HookChannel(_dir);
        var args = channel.SettingsArgsFor("web");
        Assert.Equal("--settings", args[0]);
        Assert.Contains(_dir, args[1]);
    }
}
