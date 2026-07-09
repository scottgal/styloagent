using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Core.Sessions;
using XTerm;
using XTerm.Events;
using XTerm.Options;
using AvaloniaKey = Avalonia.Input.Key;
using XTermKey = XTerm.Input.Key;
using XTermModifiers = XTerm.Input.KeyModifiers;

namespace Styloagent.Terminal;

/// <summary>
/// Avalonia control that renders a PTY session using XTerm.NET as the headless VT engine.
/// Feed it a session via <see cref="Attach"/> and it handles output display, key input, and resize.
/// </summary>
public sealed partial class TerminalControl : UserControl
{
    private readonly XTerm.Terminal _terminal;
    private readonly AvaloniaList<string> _rows = new();
    private IPtySession? _session;

    /// <summary>Observable row strings bound by the ItemsControl in XAML.</summary>
    public AvaloniaList<string> Rows => _rows;

    /// <summary>
    /// Flattened text of all rows joined with newlines.
    /// Useful for assertions: check that a known string appears anywhere in the rendered output.
    /// </summary>
    public string RenderedText => string.Join("\n", _rows);

    public TerminalControl()
    {
        InitializeComponent();

        _terminal = new XTerm.Terminal(new TerminalOptions
        {
            Cols = 80,
            Rows = 24,
            Scrollback = 1000,
        });

        // When XTerm produces data (e.g. terminal query responses), forward it to the PTY.
        _terminal.DataReceived += OnTerminalDataReceived;

        // Initialize rows to match the terminal's initial size.
        RebuildRows();

        // Start Avalonia size tracking.
        SizeChanged += OnSizeChanged;

        // Register for both Tunnel and Bubble strategies:
        // - Tunnel: captures keys as they route from the visual root down through TerminalControl,
        //   intercepting them before any child controls can handle them (correct production behavior).
        // - Bubble: also catches direct RaiseEvent calls (used in headless unit tests where the
        //   headless platform's keyboard routing produces a Bubble-only pass on the source control).
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Attach an <see cref="IPtySession"/>. Call once per session.
    /// Output and Exited callbacks fire on a background thread; this method marshals them to the UI thread.
    /// </summary>
    public void Attach(IPtySession session)
    {
        if (_session is not null)
            throw new InvalidOperationException("A session is already attached. Detach it first.");

        _session = session;
        session.Output += OnSessionOutput;
        session.Exited += OnSessionExited;
    }

    /// <summary>
    /// Detach the current session, unsubscribing events. Does not dispose the session.
    /// </summary>
    public void Detach()
    {
        if (_session is null) return;
        _session.Output -= OnSessionOutput;
        _session.Exited -= OnSessionExited;
        _session = null;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Fix 4: auto-teardown — unsubscribe session events when the control leaves the tree
        // to prevent handler leaks when the control is removed without an explicit Detach() call.
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Session event handlers (called on background thread) ────────────────

    private void OnSessionOutput(string text)
    {
        // Write to XTerm engine — Terminal.Write is safe to call from any thread.
        _terminal.Write(text);

        // Marshal the buffer rebuild to the UI thread.
        Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Render);
    }

    private void OnSessionExited()
    {
        // No UI action needed beyond potential future status display.
    }

    // ── XTerm → PTY data (terminal query responses) ─────────────────────────

    private void OnTerminalDataReceived(object? sender, TerminalEvents.DataEventArgs e)
    {
        // Fix 1: route through shared fire-and-forget helper so exceptions are never silently lost.
        FireAndForgetWrite(e.Data);
    }

    // ── Keyboard input ───────────────────────────────────────────────────────

    /// <summary>
    /// Test seam: directly invoke the key translation + write path for <paramref name="key"/>.
    /// Used in headless unit tests where Avalonia's keyboard event routing (tunnel pass from root)
    /// is not available. Translates the key to a VT sequence and writes it to the session.
    /// </summary>
    internal void SimulateKeyInput(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (_session is null) return;

        XTermModifiers mods = XTermModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0)   mods |= XTermModifiers.Shift;
        if ((modifiers & KeyModifiers.Control) != 0) mods |= XTermModifiers.Control;
        if ((modifiers & KeyModifiers.Alt) != 0)     mods |= XTermModifiers.Alt;

        XTermKey? xtermKey = key switch
        {
            AvaloniaKey.Enter    => XTermKey.Enter,
            AvaloniaKey.Back     => XTermKey.Backspace,
            AvaloniaKey.Tab      => XTermKey.Tab,
            AvaloniaKey.Escape   => XTermKey.Escape,
            AvaloniaKey.Up       => XTermKey.UpArrow,
            AvaloniaKey.Down     => XTermKey.DownArrow,
            AvaloniaKey.Left     => XTermKey.LeftArrow,
            AvaloniaKey.Right    => XTermKey.RightArrow,
            _                    => null,
        };

        if (!xtermKey.HasValue) return;

        string? vtSequence = _terminal.GenerateKeyInput(xtermKey.Value, mods);

        // XTerm.NET returns null for Enter; fall back to the standard CR sequence.
        if (vtSequence is null && xtermKey == XTermKey.Enter)
            vtSequence = "\r";

        if (vtSequence is null) return;
        FireAndForgetWrite(vtSequence);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session is null) return;

        string? vtSequence = TranslateKey(e);
        if (vtSequence is null) return;

        e.Handled = true;
        // Fix 1: route through shared fire-and-forget helper so exceptions are never silently lost.
        FireAndForgetWrite(vtSequence);
    }

    /// <summary>
    /// Fix 1: shared fire-and-forget helper for both the DataReceived forward path and the
    /// keystroke write path. Awaits WriteAsync internally and catches:
    /// - <see cref="OperationCanceledException"/> — silently ignored (session shutting down).
    /// - <see cref="Exception"/> — logged to debug output.
    ///   TODO: route write failures to a real error surface (status bar, error event) once the
    ///   shell has an appropriate error channel.
    /// </summary>
    private async void FireAndForgetWrite(string data)
    {
        if (_session is null) return;
        try
        {
            await _session.WriteAsync(data).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session is shutting down — expected, ignore.
        }
        catch (Exception ex)
        {
            // TODO: route to a real error surface (status bar / error event) in a future PR.
            System.Diagnostics.Debug.WriteLine($"[TerminalControl] write failed: {ex}");
        }
    }

    // ── Size change → PTY resize ─────────────────────────────────────────────

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Approximate: 8px per char width, 16px per row height at font size 13.
        const double charWidth = 8.0;
        const double charHeight = 16.0;

        int cols = Math.Max(10, (int)(e.NewSize.Width / charWidth));
        int rows = Math.Max(4, (int)(e.NewSize.Height / charHeight));

        if (cols != _terminal.Cols || rows != _terminal.Rows)
        {
            _terminal.Resize(cols, rows);
            _session?.Resize(cols, rows);
            RebuildRows();
        }
    }

    // ── Buffer render ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the XTerm.NET buffer via GetVisibleLines() and rebuilds the <see cref="Rows"/> list.
    /// Must be called on the UI thread.
    /// </summary>
    private void RebuildRows()
    {
        string[] lines = _terminal.GetVisibleLines();

        // Sync the observable list to the new line count.
        while (_rows.Count < lines.Length) _rows.Add(string.Empty);
        while (_rows.Count > lines.Length) _rows.RemoveAt(_rows.Count - 1);

        for (int i = 0; i < lines.Length; i++)
        {
            _rows[i] = lines[i];
        }
    }

    // ── Key translation ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts an Avalonia <see cref="AvaloniaKey"/> to the VT sequence the PTY expects,
    /// using XTerm.NET's <c>GenerateKeyInput</c> / <c>GenerateCharInput</c>.
    /// Returns null if the key should not be forwarded (e.g. modifier-only, unmapped).
    /// </summary>
    private string? TranslateKey(KeyEventArgs e)
    {
        XTermModifiers mods = XTermModifiers.None;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)   mods |= XTermModifiers.Shift;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0) mods |= XTermModifiers.Control;
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0)     mods |= XTermModifiers.Alt;

        // Map well-known Avalonia keys to XTerm.Input.Key.
        XTermKey? xtermKey = e.Key switch
        {
            AvaloniaKey.Enter    => XTermKey.Enter,
            AvaloniaKey.Back     => XTermKey.Backspace,
            AvaloniaKey.Tab      => XTermKey.Tab,
            AvaloniaKey.Escape   => XTermKey.Escape,
            AvaloniaKey.Up       => XTermKey.UpArrow,
            AvaloniaKey.Down     => XTermKey.DownArrow,
            AvaloniaKey.Left     => XTermKey.LeftArrow,
            AvaloniaKey.Right    => XTermKey.RightArrow,
            AvaloniaKey.Home     => XTermKey.Home,
            AvaloniaKey.End      => XTermKey.End,
            AvaloniaKey.Delete   => XTermKey.Delete,
            AvaloniaKey.PageUp   => XTermKey.PageUp,
            AvaloniaKey.PageDown => XTermKey.PageDown,
            AvaloniaKey.F1       => XTermKey.F1,
            AvaloniaKey.F2       => XTermKey.F2,
            AvaloniaKey.F3       => XTermKey.F3,
            AvaloniaKey.F4       => XTermKey.F4,
            _                    => null,
        };

        if (xtermKey.HasValue)
        {
            string? seq = _terminal.GenerateKeyInput(xtermKey.Value, mods);
            // XTerm.NET returns null for Enter; fall back to the standard CR sequence.
            if (seq is null && xtermKey == XTermKey.Enter)
                seq = "\r";
            return seq;
        }

        // For printable characters (including Ctrl+letter combinations), use GenerateCharInput.
        if (e.Key >= AvaloniaKey.A && e.Key <= AvaloniaKey.Z)
        {
            char ch = (e.KeyModifiers & KeyModifiers.Shift) != 0
                ? (char)('A' + (e.Key - AvaloniaKey.A))
                : (char)('a' + (e.Key - AvaloniaKey.A));
            return _terminal.GenerateCharInput(ch, mods);
        }

        // Space
        if (e.Key == AvaloniaKey.Space)
            return _terminal.GenerateCharInput(' ', mods);

        return null;
    }
}
