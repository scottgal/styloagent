using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Sessions;

/// <summary>Extracts Codex's terminal-only command approval frame when no hook event is emitted.</summary>
public sealed class CodexApprovalDetector
{
    private const string Heading = "Would you like to run the following command?";
    private const string Confirm = "Press enter to confirm or esc to cancel";
    private const int MaxBuffer = 16 * 1024;
    private string _buffer = string.Empty;
    private string? _activeId;

    /// <summary>Raised with a prompt when approval is requested, or null when it is resolved.</summary>
    public event Action<CodexApprovalPrompt?>? Changed;

    public CodexApprovalPrompt? Current { get; private set; }

    public void Feed(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _buffer = (_buffer + StripAnsi(chunk));
        if (_buffer.Length > MaxBuffer) _buffer = _buffer[^MaxBuffer..];

        int start = _buffer.LastIndexOf(Heading, StringComparison.OrdinalIgnoreCase);
        int end = _buffer.LastIndexOf(Confirm, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start) return;

        var frame = _buffer[start..(end + Confirm.Length)].Trim();
        var prompt = Parse(frame);
        if (prompt.RequestId == _activeId) return;
        _activeId = prompt.RequestId;
        Current = prompt;
        Changed?.Invoke(prompt);
    }

    /// <summary>Call immediately after the operator chooses an approval/cancel action.</summary>
    public void Resolve()
    {
        if (Current is null) return;
        Current = null;
        _activeId = null;
        Changed?.Invoke(null);
    }

    private static CodexApprovalPrompt Parse(string frame)
    {
        var normalized = Regex.Replace(frame, @"\s+", " ").Trim();
        var options = Regex.Matches(frame, @"(?m)^\s*([1-9]\d*)[.)]\s*(.+?)\s*$")
            .Select(m => m.Groups[2].Value.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
        var command = frame[(frame.IndexOf(Heading, StringComparison.OrdinalIgnoreCase) + Heading.Length)..]
            .Replace(Confirm, "", StringComparison.OrdinalIgnoreCase).Trim();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return new CodexApprovalPrompt(id, command, "enter-confirm", options);
    }

    private static string StripAnsi(string value)
        => Regex.Replace(value, "\\x1B(?:[@-_][0-?]*[ -/]*[@-~]|\\[[0-?]*[ -/]*[@-~])", "");
}

/// <param name="RequestId">Stable digest for this displayed approval frame.</param>
/// <param name="Prompt">The command/question text shown by Codex.</param>
/// <param name="SelectionMode">How the default selection is confirmed (currently <c>enter-confirm</c>).</param>
/// <param name="Options">Visible numbered choices, in terminal order.</param>
public sealed record CodexApprovalPrompt(string RequestId, string Prompt, string SelectionMode, IReadOnlyList<string> Options);
