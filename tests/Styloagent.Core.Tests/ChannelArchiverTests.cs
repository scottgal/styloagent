using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

public sealed class ChannelArchiverTests
{
    [Fact]
    public void ArchiveThreads_handles_dashed_prefixes_and_moves_only_requested_slugs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var outbox = Path.Combine(root, "outbox");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outbox);

        try
        {
            File.WriteAllText(Path.Combine(inbox, "agent-12-release-status.md"), "release");
            File.WriteAllText(Path.Combine(inbox, "agent-12-follow-up-release-status.md"), "follow-up");
            File.WriteAllText(Path.Combine(outbox, "release-status.reply.md"), "reply");
            File.WriteAllText(Path.Combine(inbox, "agent-12-unrelated.md"), "keep");

            var moved = ChannelArchiver.ArchiveThreads(root, ["release-status"]);

            Assert.Equal(3, moved);
            Assert.True(File.Exists(Path.Combine(inbox, "agent-12-unrelated.md")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "archive", "inbox"), "*.md").Length);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "archive", "outbox"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The close/archive round trip for a thread whose subject exceeds the writer's 48-character cap.
    /// The fixture is built by CALLING THE WRITER rather than by hand-making filenames, so the test
    /// exercises the names the product actually creates: `Reply` writes outbox/&lt;Slug(thread)&gt;.reply.md,
    /// while the closer passes the raw thread text. Before the archiver keyed on the slugged form too,
    /// that gap stranded the record permanently, which is what this pins.
    /// </summary>
    [Fact]
    public void ArchiveThread_sweeps_both_halves_of_an_over_48_argument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var thread = "a thread subject long enough that the writer caps its slug at forty eight characters";

        // The fixture is only the defect's shape if the mechanism really caps here.
        Assert.True(thread.Length > 48, "fixture must exceed the writer's cap to exercise the cap path");

        try
        {
            ChannelMessageWriter.Reply(root, "overview-", thread, "done", DateTimeOffset.UnixEpoch);
            ChannelMessageWriter.Write(root, "queue-", "overview-", thread, "please look", "normal", DateTimeOffset.UnixEpoch);

            // The caller passes the RAW thread text, exactly as MainWindowViewModel.cs:2128 does.
            var moved = ChannelArchiver.ArchiveThread(root, thread);

            Assert.Equal(2, moved);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "outbox"), "*.md"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "inbox"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The operator dismiss path passes a display Subject (BusViewModel.cs:536), which carries punctuation
    /// and capitals and never equals a dash-delimited slug. Keyed on the slugged form, the record the
    /// writer named for that same subject is now reachable and the dismiss actually moves something.
    /// </summary>
    [Fact]
    public void ArchiveThread_sweeps_the_record_when_the_caller_passes_a_display_subject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var subject = "Correction: the 25 of 36 in my previous message was wrong.";

        try
        {
            ChannelMessageWriter.Reply(root, "overview-", subject, "done", DateTimeOffset.UnixEpoch);

            var moved = ChannelArchiver.ArchiveThread(root, subject);

            Assert.Equal(1, moved);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "outbox"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The arm is "the argument differs from its slug", NOT "the argument is over the 48 cap". Slug
    /// lowercases and drops every character that is not a letter or digit, so a subject carrying capitals
    /// or punctuation is not its own slug AT ANY LENGTH. This fixture is deliberately under the cap so a
    /// pass cannot come from the length, and the assertion that the record's name differs from the subject
    /// is what proves the fixture sits in that arm.
    /// </summary>
    [Fact]
    public void ArchiveThread_sweeps_a_subject_under_48_that_is_not_its_own_slug()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var subject = "Gate r62: total moved.";

        Assert.True(subject.Length < 48, "fixture must be under the cap so the length cannot explain a pass");

        try
        {
            ChannelMessageWriter.Reply(root, "overview-", subject, "done", DateTimeOffset.UnixEpoch);
            ChannelMessageWriter.Write(root, "queue-", "overview-", subject, "note", "normal", DateTimeOffset.UnixEpoch);

            var record = Directory.GetFiles(Path.Combine(root, "outbox"), "*.md").Single();
            Assert.NotEqual(subject + ".reply.md", Path.GetFileName(record));

            var moved = ChannelArchiver.ArchiveThread(root, subject);

            Assert.Equal(2, moved);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "outbox"), "*.md"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "inbox"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A broadcast copy is never archived. Every lane holds one, so sweeping it removes the message from
    /// every live queue at once. The key expansion is what makes this reachable rather than theoretical:
    /// `all-&lt;slug&gt;` is named from the same slug the archiver now also keys on, so without the guard a
    /// close would take the broadcast with it whenever the subject matched.
    /// </summary>
    [Fact]
    public void ArchiveThread_never_moves_a_broadcast_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"styloagent-archive-{Guid.NewGuid():N}");
        var subject = "a broadcast every lane receives";

        try
        {
            ChannelMessageWriter.Write(root, "queue-", "all-", subject, "fyi", "info", DateTimeOffset.UnixEpoch);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "inbox"), "all-*.md"));

            // The same subject, passed as a closer would: the slug form is in the key set, so only the
            // broadcast guard can stop this from moving.
            var moved = ChannelArchiver.ArchiveThread(root, subject);

            Assert.Equal(0, moved);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "inbox"), "all-*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
