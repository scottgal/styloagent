namespace Styloagent.Terminal;

/// <summary>
/// A per-terminal colour theme: default background + foreground (ARGB). Applied to a
/// <see cref="TerminalControl"/> so each agent's terminal can wear its own look.
/// </summary>
public sealed record TerminalTheme(string Name, uint Background, uint Foreground)
{
    public static readonly TerminalTheme Default   = new("Default",   0xFF0C0C0C, 0xFFEDEDED);
    public static readonly TerminalTheme Solarized = new("Solarized", 0xFF002B36, 0xFF93A1A1);
    public static readonly TerminalTheme Matrix    = new("Matrix",    0xFF000000, 0xFF00FF41);
    public static readonly TerminalTheme Dracula   = new("Dracula",   0xFF282A36, 0xFFF8F8F2);
    public static readonly TerminalTheme Light     = new("Light",     0xFFFAFAF7, 0xFF1A1A1A);

    /// <summary>All built-in themes, in picker order.</summary>
    public static readonly IReadOnlyList<TerminalTheme> All =
        new[] { Default, Solarized, Matrix, Dracula, Light };
}
