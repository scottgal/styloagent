using System.Text;

namespace Styloagent.Terminal;

/// <summary>
/// Drops unsupported DCS/SOS/PM/APC strings before XTerm.NET 1.0.15 parses them.
/// Its APC state never exits on ST (e.g. OpenTUI's Kitty graphics capability probe),
/// swallowing all subsequent output. Keep CSI/OSC and ordinary text intact.
/// </summary>
internal sealed class TerminalControlStringFilter
{
    private enum State { Ground, Escape, String, StringEscape }
    private State _state;

    public string Filter(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (_state)
            {
                case State.Ground:
                    if (ch == '\u001b') _state = State.Escape;
                    else if (ch is '\u0090' or '\u0098' or '\u009e' or '\u009f') _state = State.String;
                    else output.Append(ch);
                    break;
                case State.Escape:
                    if (ch is 'P' or 'X' or '^' or '_') _state = State.String;
                    else
                    {
                        output.Append('\u001b');
                        if (ch != '\u001b') { output.Append(ch); _state = State.Ground; }
                    }
                    break;
                case State.String:
                    if (ch is '\u009c' or '\u0018' or '\u001a') _state = State.Ground;
                    else if (ch == '\u001b') _state = State.StringEscape;
                    break;
                case State.StringEscape:
                    if (ch is '\\' or '\u009c' or '\u0018' or '\u001a') _state = State.Ground;
                    else if (ch != '\u001b') _state = State.String;
                    break;
            }
        }
        return output.ToString();
    }
}
