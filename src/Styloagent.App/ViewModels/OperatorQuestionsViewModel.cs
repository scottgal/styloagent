using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Attention;

namespace Styloagent.App.ViewModels;

/// <summary>
/// One pending operator question in the top-bar banner: the asking agent, the question text, and its
/// one-click answer options. Clicking an option routes the choice back to the asker via the hub.
/// </summary>
public sealed partial class OperatorQuestionItem : ObservableObject
{
    private readonly Func<string, string, Task<bool>> _answer;   // (askingPrefix, chosenOption)

    public string AskingPrefix { get; }
    public string Question { get; }
    public IReadOnlyList<string> Options { get; }

    public OperatorQuestionItem(OperatorQuestion q, Func<string, string, Task<bool>> answer)
    {
        AskingPrefix = q.AskingPrefix;
        Question     = q.Question;
        Options      = q.Options;
        _answer      = answer;
    }

    /// <summary>One-click answer: deliver <paramref name="option"/> back to the asker and clear the question.</summary>
    [RelayCommand]
    private Task Answer(string? option)
        => string.IsNullOrEmpty(option) ? Task.CompletedTask : _answer(AskingPrefix, option);
}

/// <summary>
/// View-model for the fleet-wide operator-question top bar. Mirrors <see cref="OperatorQuestionHub"/>'s
/// pending set into an observable list the banner binds to, and routes one-click answers back through the
/// hub. Deliberately does NOT touch pane state — the pane's <c>PendingOperatorQuestion</c> indicator is
/// reconciled separately by the shell so the hook-driven HookState (which the delivery service reads) stays
/// honest; forcing WaitingForHuman here would make delivery defer the very answer meant to wake the asker.
/// </summary>
public sealed partial class OperatorQuestionsViewModel : ObservableObject, IDisposable
{
    private readonly OperatorQuestionHub _hub;
    private bool _disposed;

    /// <summary>Pending questions, oldest-asked first (top-bar order).</summary>
    public ObservableCollection<OperatorQuestionItem> Questions { get; } = new();

    /// <summary>Banner visibility — true while any question is pending.</summary>
    public bool HasQuestions => Questions.Count > 0;

    /// <summary>Raised after the pending set is reconciled, so the shell can sync per-pane indicators.</summary>
    public event Action? PendingChanged;

    public OperatorQuestionsViewModel(OperatorQuestionHub hub)
    {
        _hub = hub;
        _hub.Changed += OnHubChanged;
        Reconcile();
    }

    /// <summary>Prefix → question text for every pending ask — the shell reconciles pane indicators off this.</summary>
    public IReadOnlyDictionary<string, string> PendingByPrefix
        => Questions.ToDictionary(q => q.AskingPrefix, q => q.Question);

    // Changed fires on the MCP server thread — marshal to the UI thread before touching the collection.
    private void OnHubChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) Reconcile();
            else Dispatcher.UIThread.Post(Reconcile);
        }
        catch
        {
            Reconcile();   // no dispatcher (headless/test) — reconcile directly
        }
    }

    private void Reconcile()
    {
        Questions.Clear();
        foreach (var q in _hub.Pending)
            Questions.Add(new OperatorQuestionItem(q, _hub.AnswerAsync));
        OnPropertyChanged(nameof(HasQuestions));
        PendingChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hub.Changed -= OnHubChanged;
    }
}
