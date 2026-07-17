using Styloagent.App.ViewModels;

namespace Styloagent.App.Tests;

/// <summary>
/// The operator-side "seen / archived" view-state seam for the bus viewer.
/// bus- owns the real Core store + on-disk representation; this exercises the App-side FAKE
/// (<see cref="InMemoryBusViewState"/>) the viewer binds to today. The contract asserted here is
/// what overview- will relay bus-'s real API against, so the fake can be swapped 1:1.
/// </summary>
public class BusViewStateTests
{
    private static readonly DateTimeOffset T1 = new(2024, 1, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2024, 1, 10, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshStore_ThreadIsNeitherSeenNorArchived()
    {
        var store = new InMemoryBusViewState();
        Assert.False(store.IsSeen("alpha-q", T1));
        Assert.False(store.IsArchived("alpha-q"));
    }

    [Fact]
    public void MarkSeen_MakesThreadSeen_UpToThatActivity()
    {
        var store = new InMemoryBusViewState();
        store.MarkSeen("alpha-q", T1);
        Assert.True(store.IsSeen("alpha-q", T1));
    }

    [Fact]
    public void NewActivity_AfterSeenWatermark_RevertsToUnseen()
    {
        // Operator saw the thread at T1; a fresh inbound at T2 (> watermark) must un-see it → WAITING again.
        var store = new InMemoryBusViewState();
        store.MarkSeen("alpha-q", T1);
        Assert.False(store.IsSeen("alpha-q", T2));
    }

    [Fact]
    public void MarkSeen_IsKeyedPerThread()
    {
        var store = new InMemoryBusViewState();
        store.MarkSeen("alpha-q", T1);
        Assert.False(store.IsSeen("beta-other", T1));
    }

    [Fact]
    public void Archive_MarksThreadArchived()
    {
        var store = new InMemoryBusViewState();
        store.Archive("alpha-q");
        Assert.True(store.IsArchived("alpha-q"));
    }

    [Fact]
    public void Keys_AreCaseInsensitive()
    {
        var store = new InMemoryBusViewState();
        store.MarkSeen("Alpha-Q", T1);
        store.Archive("Alpha-Q");
        Assert.True(store.IsSeen("alpha-q", T1));
        Assert.True(store.IsArchived("alpha-q"));
    }

    [Fact]
    public void MarkSeen_And_Archive_RaiseChanged()
    {
        var store = new InMemoryBusViewState();
        int changed = 0;
        store.Changed += () => changed++;
        store.MarkSeen("alpha-q", T1);
        store.Archive("alpha-q");
        Assert.Equal(2, changed);
    }
}
