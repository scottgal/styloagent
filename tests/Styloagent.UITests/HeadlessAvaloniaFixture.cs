using Avalonia;
using Avalonia.Headless;

namespace Styloagent.UITests;

/// <summary>
/// xUnit collection fixture that boots a single Avalonia headless session for the
/// whole test run. Tests opt in via [Collection("Avalonia")] and use
/// DispatchAsync to marshal work onto the headless UI thread.
/// </summary>
public sealed class HeadlessAvaloniaFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessAvaloniaFixture()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
    }

    public Task<T> DispatchAsync<T>(Func<T> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<Task<T>> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Action work) =>
        _session.Dispatch(work, CancellationToken.None);

    public Task DispatchAsync(Func<Task> work) =>
        _session.Dispatch(work, CancellationToken.None);

    public void Dispose() => _session.Dispose();
}

/// <summary>Minimal Avalonia application used to bootstrap the headless platform.</summary>
public sealed class TestApp : Application
{
    public override void Initialize() { }
}

/// <summary>Marker so all Avalonia tests share one headless session.</summary>
[CollectionDefinition("Avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvaloniaFixture> { }
