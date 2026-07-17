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

/// <summary>
/// The durable, file-backed <see cref="IBusViewState"/> the shipped viewer uses — operator seen/archived
/// read-state persisted to a small JSON under <c>.styloagent/</c> so the operator's Archive (and seen)
/// survive a cockpit restart. No Core verb, no channel-file moves (per overview-'s ruling).
/// </summary>
public class JsonBusViewStateTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2024, 1, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2024, 1, 10, 11, 0, 0, TimeSpan.Zero);
    private readonly string _dir;
    private readonly string _path;

    public JsonBusViewStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "busvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "bus-view-state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Persists_SeenAndArchived_AcrossInstances()
    {
        var a = new JsonBusViewState(_path);
        a.MarkSeen("alpha-q", T1);
        a.Archive("beta-x");

        // A fresh instance over the same file (simulating a cockpit restart) sees the persisted state.
        var b = new JsonBusViewState(_path);
        Assert.True(b.IsSeen("alpha-q", T1));
        Assert.True(b.IsArchived("beta-x"));
    }

    [Fact]
    public void Archived_SurvivesRestart_EvenWithNoSeen()
    {
        new JsonBusViewState(_path).Archive("gamma-y");
        Assert.True(new JsonBusViewState(_path).IsArchived("gamma-y"));
    }

    [Fact]
    public void MissingFile_IsEmpty_NoThrow()
    {
        var s = new JsonBusViewState(Path.Combine(_dir, "not-created-yet.json"));
        Assert.False(s.IsArchived("x"));
        Assert.False(s.IsSeen("x", T1));
    }

    [Fact]
    public void CorruptFile_DegradesToEmpty_NoThrow()
    {
        File.WriteAllText(_path, "{ this is not valid json ]]]");
        var s = new JsonBusViewState(_path);   // must not throw
        Assert.False(s.IsArchived("x"));
        Assert.False(s.IsSeen("x", T1));
    }

    [Fact]
    public void Honors_SeenWatermark_AndCaseInsensitiveKeys_LikeContract()
    {
        var s = new JsonBusViewState(_path);
        s.MarkSeen("Alpha-Q", T1);
        Assert.True(s.IsSeen("alpha-q", T1));
        Assert.False(s.IsSeen("alpha-q", T2));   // new activity un-sees
    }

    [Fact]
    public void MarkSeen_And_Archive_RaiseChanged()
    {
        var s = new JsonBusViewState(_path);
        int changed = 0;
        s.Changed += () => changed++;
        s.MarkSeen("alpha-q", T1);
        s.Archive("alpha-q");
        Assert.Equal(2, changed);
    }
}
