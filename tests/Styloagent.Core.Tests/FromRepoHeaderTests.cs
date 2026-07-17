using Styloagent.Core.Channel;

namespace Styloagent.Core.Tests;

/// <summary>
/// Bug A (repo-qualified messaging, Model A): a message carries an optional <c>**From-Repo:**</c> header so
/// a cross-repo reply can route home. The header is additive — single-repo traffic omits it and reads back
/// <c>FromRepo == null</c>, so nothing about the released single-repo path changes.
/// </summary>
public class FromRepoHeaderTests
{
    private static readonly string[] Prefixes = { "overview-", "bus-" };

    private static string TempChannel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "styloagent-fromrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Writer_stamps_From_Repo_and_projection_reads_it_back()
    {
        var root = TempChannel();
        try
        {
            ChannelMessageWriter.Write(
                root, from: "overview-", to: "bus-", subject: "cross repo",
                body: "hello", priority: "normal", timestamp: DateTimeOffset.Now,
                fromRepo: "styloagent");

            var threads = await new ChannelProjection().ReadAsync(root, Prefixes);
            var msg = threads.SelectMany(t => t.Messages).Single();

            Assert.Equal("styloagent", msg.FromRepo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task No_From_Repo_header_leaves_FromRepo_null_back_compat()
    {
        var root = TempChannel();
        try
        {
            ChannelMessageWriter.Write(
                root, "overview-", "bus-", "intra repo", "hi", "normal", DateTimeOffset.Now);

            var threads = await new ChannelProjection().ReadAsync(root, Prefixes);
            var msg = threads.SelectMany(t => t.Messages).Single();

            Assert.Null(msg.FromRepo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Blank_fromRepo_writes_no_header()
    {
        var root = TempChannel();
        try
        {
            var path = ChannelMessageWriter.Write(
                root, "overview-", "bus-", "blank repo", "hi", "normal", DateTimeOffset.Now,
                fromRepo: "   ");

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("From-Repo", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
