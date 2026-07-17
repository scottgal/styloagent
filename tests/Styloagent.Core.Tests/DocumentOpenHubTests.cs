using Styloagent.Core.Attention;

namespace Styloagent.Core.Tests;

/// <summary>
/// The open_document hub: the verb posts a resolved open-request, the hub raises <c>Opened</c> for the cockpit
/// to open the document. Fire-and-forget (no answer routes back, nothing persisted); an unsubscribed hub is a
/// harmless no-op (graceful-degrade, like ask_operator with a null hub).
/// </summary>
public class DocumentOpenHubTests
{
    [Fact]
    public void Post_raises_Opened_with_the_request()
    {
        var hub = new DocumentOpenHub();
        DocumentOpenRequest? got = null;
        hub.Opened += (_, req) => got = req;

        var posted = hub.Post("bus-", "/repo/seam.md", "here's the seam report");

        Assert.NotNull(got);
        Assert.Equal("bus-", got!.AskingPrefix);
        Assert.Equal("/repo/seam.md", got.Path);
        Assert.Equal("here's the seam report", got.Reason);
        Assert.Equal(posted, got);
    }

    [Fact]
    public void Blank_reason_becomes_null()
    {
        var hub = new DocumentOpenHub();
        DocumentOpenRequest? got = null;
        hub.Opened += (_, req) => got = req;

        hub.Post("bus-", "/repo/doc.md", "   ");

        Assert.Null(got!.Reason);
    }

    [Fact]
    public void Post_without_a_subscriber_is_a_harmless_noop()
    {
        var hub = new DocumentOpenHub();

        var req = hub.Post("bus-", "/repo/doc.md", null);   // no listener → must not throw

        Assert.Equal("/repo/doc.md", req.Path);
    }
}
