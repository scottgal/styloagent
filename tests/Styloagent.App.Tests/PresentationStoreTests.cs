using Styloagent.App.Config;

namespace Styloagent.App.Tests;

public class PresentationStoreTests
{
    [Fact]
    public async Task RoundTrip_PreservesAllFields()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var store = new PresentationStore();
            var input = new List<AgentPresentation>
            {
                new("foss-", "FOSS Agent", "#E57373"),
                new("dash-", "Dashboard", "#4FC3F7"),
            };

            await store.SaveAsync(tmp, input);
            var loaded = await store.LoadAsync(tmp);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("foss-", loaded[0].Prefix);
            Assert.Equal("FOSS Agent", loaded[0].DisplayName);
            Assert.Equal("#E57373", loaded[0].BorderColorHex);
            Assert.Equal("dash-", loaded[1].Prefix);
            Assert.Equal("Dashboard", loaded[1].DisplayName);
            Assert.Equal("#4FC3F7", loaded[1].BorderColorHex);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void DefaultColorFor_IsDeterministic_SamePrefixSameColor()
    {
        var c1 = PresentationStore.DefaultColorFor("foss-");
        var c2 = PresentationStore.DefaultColorFor("foss-");
        Assert.Equal(c1, c2);
    }

    [Theory]
    [InlineData("foss-", "foss")]
    [InlineData("foss-", "FOSS")]
    [InlineData("foss-", "wt-foss")]
    [InlineData("foss-", "wt-foss-")]
    [InlineData("foss-", "worktree-foss")]
    public void DefaultColorFor_AlignsEquivalentIdentities(string a, string b)
    {
        // A worktree agent and its channel routing prefix must colour alike so an agent's
        // terminal/roster colour matches its bus messages.
        Assert.Equal(PresentationStore.DefaultColorFor(a), PresentationStore.DefaultColorFor(b));
    }

    [Fact]
    public void DefaultColorFor_KeepsDistinctIdentitiesDistinguishable()
    {
        // Different logical agents should not collapse to the same key.
        Assert.NotEqual(
            PresentationStore.NormalizeColorKey("foss-"),
            PresentationStore.NormalizeColorKey("dash-"));
    }

    [Fact]
    public void DefaultColorFor_IsStable_AcrossMultiplePrefixes()
    {
        // Call several times per prefix to confirm no side-effects change the result.
        var prefixes = new[] { "foss-", "dash-", "deploy-", "prod-", "mae-", "edit-", "wba-", "caps-", "overview-" };
        foreach (var prefix in prefixes)
        {
            var first = PresentationStore.DefaultColorFor(prefix);
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(first, PresentationStore.DefaultColorFor(prefix));
            }
        }
    }

    [Fact]
    public void DefaultColorFor_ReturnsHexString()
    {
        var color = PresentationStore.DefaultColorFor("any-");
        Assert.StartsWith("#", color);
        Assert.Equal(7, color.Length); // "#RRGGBB"
    }
}
