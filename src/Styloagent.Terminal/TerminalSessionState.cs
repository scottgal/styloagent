using System.Runtime.CompilerServices;
using Styloagent.Core.Sessions;
using XTerm.Options;

namespace Styloagent.Terminal;

/// <summary>
/// VT state belongs to a running PTY, not a dock view. A bounded raw tail cannot reconstruct a terminal:
/// it can start in the middle of a control string and lose alternate-screen, mouse and colour modes.
/// Keep parsing while views are absent; only Avalonia layout and painting pause.
/// </summary>
internal sealed class TerminalSessionState
{
    private static readonly ConditionalWeakTable<IPtySession, TerminalSessionState> Sessions = new();
    private readonly TerminalControlStringFilter _controlStrings = new();
    internal object Gate { get; } = new();
    internal XTerm.Terminal Engine { get; }
    internal long Generation { get; private set; }
    internal event Action? Changed;
    internal event Action<int>? Trimmed;

    internal TerminalSessionState(int cols, int rows, int scrollback)
    {
        Engine = new XTerm.Terminal(new TerminalOptions { Cols = cols, Rows = rows, Scrollback = scrollback });
        Engine.Buffer.Trimmed += count => Trimmed?.Invoke(count);
    }

    internal static TerminalSessionState For(IPtySession session, int cols, int rows, int scrollback)
    {
        lock (Sessions)
        {
            return Sessions.GetValue(session.TerminalSession, source =>
            {
                var state = new TerminalSessionState(cols, rows, scrollback);
                source.Output += state.OnOutput;
                return state;
            });
        }
    }

    private void OnOutput(string text)
    {
        lock (Gate)
        {
            Engine.Write(_controlStrings.Filter(text));
            Generation++;
        }
        Changed?.Invoke();
    }

    internal void Resize(int cols, int rows)
    {
        lock (Gate)
        {
            if (cols == Engine.Cols && rows == Engine.Rows) return;
            Engine.Resize(cols, rows);
            Generation++;
        }
        Changed?.Invoke();
    }
}
