namespace Styloagent.App.Services;

/// <summary>
/// Tracks how recently the human interacted with a terminal, so attention auto-reveal can hold off
/// while they're actively typing. The <c>Idle</c> event is raised by the shell's dispatcher timer in
/// the view model (wired in the attention-reveal task); this type owns only the recency logic.
/// </summary>
public sealed class InteractionMonitor
{
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastInput;

    public InteractionMonitor(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Raised when input has been quiet for the shell's idle window (wired in the reveal task).</summary>
    public event Action? Idle;

    public void RecordInput() => _lastInput = _clock();

    public bool IsBusy(TimeSpan window)
        => _lastInput is { } t && _clock() - t < window;

    /// <summary>Invoked by the shell's idle timer tick; re-raises <see cref="Idle"/> for subscribers.</summary>
    public void RaiseIdle() => Idle?.Invoke();
}
