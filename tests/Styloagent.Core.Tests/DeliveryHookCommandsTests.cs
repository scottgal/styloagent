using Styloagent.Core.Channel;
using Xunit;

public class DeliveryHookCommandsTests
{
    private const string HooksDir = "/tmp/hooks";
    private const string SafeId = "beta-";

    [Fact]
    public void Paths_compose_under_deliver_dir()
    {
        Assert.Equal(Path.Combine(HooksDir, "deliver"), DeliveryHookCommands.DeliverDir(HooksDir));
        Assert.Equal(Path.Combine(HooksDir, "deliver", "beta-.push"), DeliveryHookCommands.PushFile(HooksDir, SafeId));
        Assert.Equal(Path.Combine(HooksDir, "deliver", "beta-.info"), DeliveryHookCommands.InfoFile(HooksDir, SafeId));
    }

    [Fact]
    public void Stop_command_captures_stdin_guards_the_loop_and_blocks_from_the_push_file()
    {
        string drop = $"{HooksDir}/beta-__$(uuidgen).json";
        string cmd = DeliveryHookCommands.ForStop(drop, HooksDir, SafeId);

        Assert.Contains("d=$(cat)", cmd);                              // stdin captured once
        Assert.Contains(drop, cmd);                                    // observation event still dropped
        Assert.Contains("\"stop_hook_active\":true", cmd);             // infinite-block loop guard
        Assert.Contains("exit 0", cmd);                                // guard bails out
        Assert.Contains("\"decision\":\"block\",\"reason\":%s", cmd);  // forces autonomous continuation
        Assert.Contains(DeliveryHookCommands.PushFile(HooksDir, SafeId), cmd);
        Assert.Contains("mv ", cmd);                                   // atomic claim
    }

    [Fact]
    public void UserPromptSubmit_command_keeps_observe_then_surfaces_info_as_additional_context()
    {
        const string observe = "cat > \"/tmp/hooks/beta-__x.json\"";
        string cmd = DeliveryHookCommands.ForUserPromptSubmit(observe, HooksDir, SafeId);

        Assert.StartsWith(observe, cmd);                                        // observation preserved
        Assert.Contains("\"hookEventName\":\"UserPromptSubmit\"", cmd);
        Assert.Contains("\"additionalContext\":%s", cmd);                       // never forces a turn
        Assert.Contains(DeliveryHookCommands.InfoFile(HooksDir, SafeId), cmd);
        Assert.DoesNotContain("\"decision\":\"block\"", cmd);                   // surfacing, not forcing
    }
}
