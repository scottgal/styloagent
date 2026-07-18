using System.Globalization;
using Avalonia.Data.Converters;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Converters;

/// <summary>
/// True when the bound dockable is an <see cref="AgentPaneViewModel"/> — used to show the per-agent
/// ⋯ actions menu ONLY on agent tabs in the shared document-tab header (markdown / diagram / bus tabs
/// carry no agent actions, so they get just their title + close).
/// </summary>
public sealed class IsAgentPaneConverter : IValueConverter
{
    public static readonly IsAgentPaneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AgentPaneViewModel;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(IsAgentPaneConverter)} does not support ConvertBack.");
}
