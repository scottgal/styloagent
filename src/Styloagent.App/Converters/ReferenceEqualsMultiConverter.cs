using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Styloagent.App.Converters;

/// <summary>
/// True when the two bound values are the same instance. Used by the DocumentControl template to show
/// only the active document's content: each document's host binds <c>[thisDockable, ActiveDockable]</c>
/// and is visible only when they match — so N agent terminals are all kept alive but only the active
/// one renders (instead of stacking on the same surface).
/// </summary>
public sealed class ReferenceEqualsMultiConverter : IMultiValueConverter
{
    public static readonly ReferenceEqualsMultiConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 2 && ReferenceEquals(values[0], values[1]);
}
