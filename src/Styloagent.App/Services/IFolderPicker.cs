namespace Styloagent.App.Services;

/// <summary>Abstracts folder selection so the Welcome VM is testable without a real dialog.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync();
}
