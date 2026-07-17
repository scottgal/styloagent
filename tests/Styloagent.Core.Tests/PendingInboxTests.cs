using System.Text.Json;
using Styloagent.Core.Channel;
using Xunit;

public class PendingInboxTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "styloagent-pending-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Enqueue_then_drain_returns_accumulated_notes_in_order_and_clears()
    {
        var inbox = new PendingInbox(TempDir());

        inbox.Enqueue("beta-", "first push", pushing: true);
        inbox.Enqueue("beta-", "second push", pushing: true);

        Assert.True(inbox.HasPending("beta-"));
        string drained = inbox.DrainFormatted("beta-");
        Assert.Contains("first push", drained);
        Assert.Contains("second push", drained);
        Assert.True(drained.IndexOf("first push", StringComparison.Ordinal)
                    < drained.IndexOf("second push", StringComparison.Ordinal));

        Assert.False(inbox.HasPending("beta-"));
        Assert.Equal("", inbox.DrainFormatted("beta-"));   // idempotent once drained
    }

    [Fact]
    public void Drain_returns_push_notes_before_info_notes()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", "info note", pushing: false);
        inbox.Enqueue("beta-", "push note", pushing: true);

        string drained = inbox.DrainFormatted("beta-");
        Assert.True(drained.IndexOf("push note", StringComparison.Ordinal)
                    < drained.IndexOf("info note", StringComparison.Ordinal));
    }

    [Fact]
    public void Recipients_are_isolated()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", "for beta", pushing: true);

        Assert.True(inbox.HasPending("beta-"));
        Assert.False(inbox.HasPending("gamma-"));
        Assert.Equal("", inbox.DrainFormatted("gamma-"));
    }

    [Fact]
    public void Notes_with_quotes_and_newlines_survive_the_roundtrip()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", "[bus] urgent \"topic\" — read it: /a/b.md", pushing: true);

        string drained = inbox.DrainFormatted("beta-");
        Assert.Contains("[bus] urgent \"topic\" — read it: /a/b.md", drained);
    }

    [Fact]
    public void On_disk_push_file_is_a_single_json_string_so_a_shell_hook_can_embed_it_raw()
    {
        string dir = TempDir();
        var inbox = new PendingInbox(dir);
        inbox.Enqueue("beta-", "line with \"quotes\"", pushing: true);

        // The invariant the sh drain relies on: the file content is a JSON string that printf %s embeds
        // into valid JSON without any shell-side escaping (same as HookSettings' hydration file).
        string pushFile = DeliveryHookCommands.PushFile(dir, "beta-");
        string onDisk = File.ReadAllText(pushFile);
        string decoded = JsonSerializer.Deserialize<string>(onDisk)!;  // parses as a JSON string
        Assert.Contains("line with \"quotes\"", decoded);
    }
}
