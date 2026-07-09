using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Core.Sessions;
using XTerm;
using XTerm.Buffer;
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

    /// <summary>Default foreground when a cell uses the terminal's default fg (colour index 256).</summary>
    private const uint DefaultFgArgb = 0xFFEDEDED;

    /// <summary>Standard 256-colour ANSI palette (indices 0-255), built once.</summary>
    private static readonly uint[] Palette256 = BuildPalette();

    /// <summary>Cache of colour → brush so we don't allocate a brush per cell per repaint.</summary>
    private readonly Dictionary<uint, IBrush> _brushCache = new();

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

        // A terminal must be focusable to receive keyboard input at all.
        Focusable = true;

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
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Focus the terminal as soon as it appears so it's immediately typeable
        // (no need to click first). Clicking another pane moves focus to it.
        Focus();
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

    /// <summary>Clicking the terminal focuses it so it receives keyboard input.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        base.OnPointerPressed(e);
    }

    /// <summary>
    /// Printable text input — letters, DIGITS, symbols, respecting keyboard layout and shift —
    /// is forwarded straight to the PTY. Control/navigation keys go through OnKeyDown instead,
    /// so nothing is double-sent. This is what makes ordinary typing (e.g. answering "1") work.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_session is not null && !string.IsNullOrEmpty(e.Text))
        {
            FireAndForgetWrite(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
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
    /// Rebuilds the rendered screen. Keeps the plain-text <see cref="Rows"/> (for RenderedText /
    /// assertions) and rebuilds the coloured inline runs shown in the SelectableTextBlock.
    /// Must be called on the UI thread.
    /// </summary>
    private void RebuildRows()
    {
        // Plain-text rows — kept for RenderedText and test assertions.
        string[] lines = _terminal.GetVisibleLines();
        while (_rows.Count < lines.Length) _rows.Add(string.Empty);
        while (_rows.Count > lines.Length) _rows.RemoveAt(_rows.Count - 1);
        for (int i = 0; i < lines.Length; i++)
            _rows[i] = lines[i];

        // Coloured render: walk the XTerm cell grid and emit one Run per contiguous
        // same-colour span. A single control with inline runs renders reliably (an
        // ItemsControl-of-rows silently failed to materialize).
        BuildColoredInlines();
    }

    /// <summary>
    /// Walks the visible XTerm cell grid and rebuilds <c>ScreenText.Inlines</c> as coloured
    /// <see cref="Run"/> spans — consecutive cells sharing a foreground colour become one Run.
    /// Falls back gracefully (default colour) if the buffer can't be read.
    /// </summary>
    private void BuildColoredInlines()
    {
        var inlines = ScreenText.Inlines;
        if (inlines is null)
        {
            inlines = new InlineCollection();
            ScreenText.Inlines = inlines;
        }
        inlines.Clear();

        TerminalBuffer buffer = _terminal.Buffer;
        int rows = _terminal.Rows;
        int yDisp = buffer.YDisp;

        var runText = new StringBuilder();

        for (int row = 0; row < rows; row++)
        {
            BufferLine? line = SafeLine(buffer, yDisp + row);
            int cols = line?.Length ?? 0;

            // Trim trailing blank cells so we don't emit a full-width run of spaces per line.
            int lastNonBlank = -1;
            for (int col = cols - 1; col >= 0; col--)
            {
                string c = line![col].Content;
                if (!string.IsNullOrEmpty(c) && c != " ") { lastNonBlank = col; break; }
            }

            runText.Clear();
            uint runColor = DefaultFgArgb;
            bool runOpen = false;

            for (int col = 0; col <= lastNonBlank; col++)
            {
                BufferCell cell = line![col];
                if (cell.Width == 0) continue; // wide-char continuation cell — skip

                string content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
                uint color = ResolveFgColor(cell.Attributes);

                if (!runOpen) { runColor = color; runOpen = true; }
                else if (color != runColor) { FlushRun(inlines, runText, runColor); runColor = color; }

                runText.Append(content);
            }

            if (runOpen) FlushRun(inlines, runText, runColor);

            // Newline separator between rows (not after the final row).
            if (row < rows - 1)
                inlines.Add(new Run("\n"));
        }
    }

    private void FlushRun(InlineCollection inlines, StringBuilder text, uint color)
    {
        if (text.Length == 0) return;
        inlines.Add(new Run(text.ToString()) { Foreground = BrushFor(color) });
        text.Clear();
    }

    private static BufferLine? SafeLine(TerminalBuffer buffer, int index)
    {
        try { return buffer.Lines[index]; }
        catch { return null; }
    }

    /// <summary>
    /// Resolves an XTerm cell's foreground to a packed 0xAARRGGBB colour.
    /// Colour mode 1 = 24-bit truecolor (packed RGB); mode 0 with index 0-255 = palette;
    /// index 256 (default fg) / 257 (default bg) fall back to the default foreground.
    /// </summary>
    private static uint ResolveFgColor(AttributeData attr)
    {
        int mode = attr.GetFgColorMode();
        int c = attr.GetFgColor();

        if (mode == 1) // RGB truecolor — c is packed 0xRRGGBB
            return 0xFF000000u | (uint)(c & 0xFFFFFF);
        if (c >= 0 && c <= 255)
            return Palette256[c];
        return DefaultFgArgb; // 256 = default fg (and any other sentinel)
    }

    private IBrush BrushFor(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out IBrush? brush)) return brush;
        brush = new SolidColorBrush(Color.FromUInt32(argb));
        _brushCache[argb] = brush;
        return brush;
    }

    /// <summary>Builds the standard xterm 256-colour palette as packed 0xAARRGGBB values.</summary>
    private static uint[] BuildPalette()
    {
        var p = new uint[256];

        // 0-15: standard + bright ANSI colours.
        uint[] basic =
        {
            0xFF000000, 0xFFCD0000, 0xFF00CD00, 0xFFCDCD00,
            0xFF0000EE, 0xFFCD00CD, 0xFF00CDCD, 0xFFE5E5E5,
            0xFF7F7F7F, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00,
            0xFF5C5CFF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
        };
        for (int i = 0; i < 16; i++) p[i] = basic[i];

        // 16-231: 6×6×6 colour cube.
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++)
        {
            int r = steps[(i / 36) % 6];
            int g = steps[(i / 6) % 6];
            int b = steps[i % 6];
            p[16 + i] = 0xFF000000u | (uint)((r << 16) | (g << 8) | b);
        }

        // 232-255: 24-step grayscale ramp.
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + 10 * i;
            p[232 + i] = 0xFF000000u | (uint)((v << 16) | (v << 8) | v);
        }

        return p;
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

        // Ctrl/Alt + letter -> control/meta sequence (e.g. Ctrl+C). Plain printable characters
        // (letters, digits, symbols, space) are delivered via OnTextInput — NOT here — so they
        // are not double-sent and respect the keyboard layout.
        if (e.Key >= AvaloniaKey.A && e.Key <= AvaloniaKey.Z
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0)
        {
            char ch = (char)('a' + (e.Key - AvaloniaKey.A));
            return _terminal.GenerateCharInput(ch, mods);
        }

        return null;
    }
}
