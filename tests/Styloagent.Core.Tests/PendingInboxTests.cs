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

    // ---- picked-up derivation (bus viewer "being-worked-on" pill) --------------
    // A message is picked up once it was delivered to the pending queue AND its note no longer waits there
    // (the recipient's turn-boundary hook — or check_inbox/DrainFormatted — drained it). The note carries
    // its own FilePath verbatim, so its absence from the live deliver files is what marks the drain.

    private const string Path1 = "/ch/inbox/beta-topic.md";

    [Fact]
    public void Delivered_note_is_not_picked_up_until_it_drains()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", $"[bus] urgent \"topic\" — read it: {Path1}", pushing: true, deliveredPath: Path1);

        Assert.False(inbox.PickedUp("beta-", Path1));   // still queued → waiting, not picked up

        inbox.DrainFormatted("beta-");                  // the recipient (or check_inbox) drains it
        Assert.True(inbox.PickedUp("beta-", Path1));    // now picked up
    }

    [Fact]
    public void Info_note_pickup_flips_when_the_info_file_drains()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", $"[bus] low \"topic\" — read it: {Path1}", pushing: false, deliveredPath: Path1);

        Assert.False(inbox.PickedUp("beta-", Path1));
        inbox.DrainFormatted("beta-");
        Assert.True(inbox.PickedUp("beta-", Path1));
    }

    [Fact]
    public void A_path_never_delivered_is_not_picked_up()
    {
        var inbox = new PendingInbox(TempDir());
        Assert.False(inbox.PickedUp("beta-", Path1));   // nothing delivered → not picked up (viewer shows WAITING)
    }

    [Fact]
    public void MarkDelivered_without_a_queued_note_is_picked_up_immediately()
    {
        // The idle-inject path: the message is injected, never queued for a hook, so nothing is left pending.
        var inbox = new PendingInbox(TempDir());
        inbox.MarkDelivered("beta-", Path1);
        Assert.True(inbox.PickedUp("beta-", Path1));
    }

    [Fact]
    public void Pickup_is_isolated_per_recipient()
    {
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", $"read it: {Path1}", pushing: true, deliveredPath: Path1);
        inbox.DrainFormatted("beta-");

        Assert.True(inbox.PickedUp("beta-", Path1));
        Assert.False(inbox.PickedUp("gamma-", Path1));  // gamma- was never delivered this path
    }

    [Fact]
    public void A_second_delivered_note_does_not_mark_the_first_picked_up_while_it_still_waits()
    {
        // Two distinct messages to the same recipient: draining is all-at-once, but until the drain both
        // remain pending, and neither is picked up.
        const string path2 = "/ch/inbox/beta-other.md";
        var inbox = new PendingInbox(TempDir());
        inbox.Enqueue("beta-", $"read it: {Path1}", pushing: true, deliveredPath: Path1);
        inbox.Enqueue("beta-", $"read it: {path2}", pushing: true, deliveredPath: path2);

        Assert.False(inbox.PickedUp("beta-", Path1));
        Assert.False(inbox.PickedUp("beta-", path2));

        inbox.DrainFormatted("beta-");
        Assert.True(inbox.PickedUp("beta-", Path1));
        Assert.True(inbox.PickedUp("beta-", path2));
    }
}
