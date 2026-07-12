using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;

namespace Styloagent.App.ViewModels;

/// <summary>
/// A bus thread opened as a document tab: an Avalonia <c>Carousel</c> pages through the thread's messages,
/// each rendered as full markdown, with prev/next + a page indicator. The "carousel through the messages in
/// a single bus" surface. It IS a Dock <see cref="Document"/> so the DockControl hosts it directly.
/// </summary>
public partial class BusThreadDocumentViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    // Present immediately rather than via Dock's Background-priority deferred queue (mirrors MarkdownDocument).
    public bool DeferContentPresentation => false;

    /// <summary>The thread's subject (tab caption + header).</summary>
    public string Subject { get; }

    /// <summary>The messages paged through by the carousel (oldest → newest as classified).</summary>
    public IReadOnlyList<BusMessageItem> Messages { get; }

    /// <summary>The carousel's current page (two-way bound to <c>Carousel.SelectedIndex</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    private int _selectedIndex;

    /// <summary>"3 / 12" — which message of the thread is showing.</summary>
    public string PageIndicator => Messages.Count == 0 ? "" : $"{SelectedIndex + 1} / {Messages.Count}";

    public BusThreadDocumentViewModel(BusThreadItem thread)
    {
        Id = "BusThread-" + Guid.NewGuid().ToString("N");
        Title = string.IsNullOrWhiteSpace(thread.Subject) ? "thread" : thread.Subject;
        Subject = Title;
        Messages = thread.Messages;
        CanFloat = true;
    }

    [RelayCommand]
    private void Next()
    {
        if (SelectedIndex < Messages.Count - 1) SelectedIndex++;
    }

    [RelayCommand]
    private void Prev()
    {
        if (SelectedIndex > 0) SelectedIndex--;
    }
}
