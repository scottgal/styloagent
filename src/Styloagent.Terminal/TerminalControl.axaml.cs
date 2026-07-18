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
    // Not readonly: re-created on each Attach so a re-attach replays the backlog into a CLEAN engine
    // (see ResetEngine / Attach). Every reader touches this field, so the swap is picked up everywhere.
    private XTerm.Terminal _terminal;
    private readonly AvaloniaList<string> _rows = new();
    private IPtySession? _session;

    /// <summary>
    /// Whether the viewport is pinned to the tail. True while the operator is at (or near) the bottom, so
    /// new output auto-scrolls to keep the live prompt/last line visible; false once they scroll up to
    /// review scrollback, so their position is preserved. Recomputed from the ScrollViewer on every scroll.
    /// </summary>
    private bool _followTail = true;

    /// <summary>Default foreground when a cell uses the terminal's default fg (colour index 256).</summary>
    private const uint DefaultFgArgb = 0xFFEDEDED;

    /// <summary>Default background (matches the control's own background); index 257 resolves here.</summary>
    private const uint DefaultBgArgb = 0xFF0C0C0C;

    /// <summary>Standard 256-colour ANSI palette (indices 0-255), built once.</summary>
    private static readonly uint[] Palette256 = BuildPalette();

    // Per-instance default fg/bg (seeded from the consts) so each terminal can wear its own theme.
    private uint _defaultFg = DefaultFgArgb;
    private uint _defaultBg = DefaultBgArgb;

    /// <summary>Applies a per-terminal colour theme (default fg/bg + the control background).</summary>
    public void ApplyTheme(TerminalTheme theme)
    {
        _defaultFg = theme.Foreground;
        _defaultBg = theme.Background;
        Background = BrushFor(_defaultBg);
        Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Render);
    }

    /// <summary>EFFECTIVE terminal font size in points (app-wide base × <see cref="ZoomLevel"/>) — drives the text and the PTY col/row cell metrics.</summary>
    private double _fontSize = 13.0;
    private const double MinFontPt = 8.0, MaxFontPt = 32.0;

    // Measured monospace cell (px), from the ACTUAL typeface — not a guessed ratio. The PTY grid and the
    // rendered text must use the SAME cell, or claude's full-width TUI wraps/overlaps ("sizing off"). Seeded
    // with sane 13pt defaults; replaced by MeasureCell() as soon as the font system is up.
    private double _cellW = 7.8;
    private double _cellH = 16.0;

    // The ScrollViewer's Padding="12,4,6,4" (see XAML): 12+6 horizontal (roomier LEFT gutter), 4+4 vertical.
    // The grid must fit the padded content box, not the full control, or cols/rows are overestimated and text
    // wraps off the edge. Keep these two in lockstep with the XAML Padding.
    private const double PadX = 18.0;
    private const double PadY = 8.0;

    // ── Zoom (per-terminal font scale; a cockpit slider binds ZoomLevel) ──────
    /// <summary>Smallest / largest / step zoom factor — the range a bound zoom slider should use.</summary>
    public const double MinZoom = 0.5, MaxZoom = 3.0, ZoomStep = 0.1;

    /// <summary>
    /// Per-terminal font zoom factor (1.0 = the app-wide terminal font size). The EFFECTIVE font size is the
    /// app-wide base × this, so zoom composes with the global size preference. Bindable — a cockpit zoom
    /// slider drives it — and clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]. Changing it
    /// re-measures the cell, refits the grid and resizes the PTY, so rendered cols == PTY cols at any zoom.
    /// </summary>
    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(ZoomLevel), 1.0, coerce: CoerceZoom);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    private static double CoerceZoom(AvaloniaObject _, double v) => Math.Clamp(v, MinZoom, MaxZoom);

    /// <summary>Zoom in one step (larger text → fewer cols/rows).</summary>
    public void ZoomIn() => ZoomLevel += ZoomStep;

    /// <summary>Zoom out one step (smaller text → more cols/rows).</summary>
    public void ZoomOut() => ZoomLevel -= ZoomStep;

    /// <summary>
    /// Applies the effective font size (app-wide base × <see cref="ZoomLevel"/>): sets the render font,
    /// re-measures the monospace cell, refits the PTY grid to the new cell (only once the control is
    /// measured), and rebuilds. The single place font / zoom / global-size changes converge.
    /// </summary>
    private void ApplyFontMetrics()
    {
        _fontSize = Math.Clamp(_globalFontSize * ZoomLevel, MinFontPt, MaxFontPt);
        ScreenText.FontSize = _fontSize;
        MeasureCell();
        if (Bounds.Width > 0 && Bounds.Height > 0)
            RefitGrid(Bounds.Size);
        Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Render);
    }

    /// <summary>Zoom changes recompute the font metrics and refit the PTY grid.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomLevelProperty)
            ApplyFontMetrics();
    }

    /// <summary>
    /// Measures the real monospace cell (advance width + line height) from the current typeface at the
    /// current font size, and renders the text at the measured line height. Using the measured cell for BOTH
    /// the render and the PTY grid is what stops the "sizing off" corruption. Keeps last-known-good on failure.
    /// </summary>
    private void MeasureCell()
    {
        try
        {
            var typeface = new Typeface(ScreenText.FontFamily);
            var ft = new FormattedText(
                new string('0', 20),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface, _fontSize, Brushes.White);
            double w = ft.WidthIncludingTrailingWhitespace / 20.0;
            double h = ft.Height;
            if (w > 0.5) _cellW = w;
            if (h > 1.0) _cellH = h;
            ScreenText.LineHeight = _cellH;   // render at the measured line height → no vertical overlap
        }
        catch { /* font system not ready — keep the last-known-good cell */ }
    }

    // ── App-wide terminal font size ──────────────────────────────────────────
    // A single global size (a user preference), so every live terminal tracks it without threading
    // per-pane state through the three pane-creation sites. New terminals pick it up in their ctor.
    private static double _globalFontSize = 13.0;

    // Live terminals that follow the app-wide font size, held WEAKLY. The old design was a static event with
    // strong instance handlers, unsubscribed only in OnDetachedFromVisualTree — which never fires for a
    // control orphaned by a layout rebuild. That pinned every orphaned TerminalControl (and its ~1000-row
    // scrollback + Skia/composition resources) for the app's lifetime: the classic .NET static-event leak.
    // Weak references let an orphaned control be collected even if no detach ever fires; SetGlobalFontSize and
    // the register path prune dead entries as they go, so the list can't grow without bound.
    private static readonly object _fontTrackersGate = new();
    private static readonly List<WeakReference<TerminalControl>> _fontTrackers = new();

    private static void TrackGlobalFontSize(TerminalControl control)
    {
        lock (_fontTrackersGate)
        {
            _fontTrackers.RemoveAll(w => !w.TryGetTarget(out _));
            _fontTrackers.Add(new WeakReference<TerminalControl>(control));
        }
    }

    private static void UntrackGlobalFontSize(TerminalControl control)
    {
        lock (_fontTrackersGate)
            _fontTrackers.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, control));
    }

    /// <summary>Sets the app-wide terminal font size; every live terminal updates immediately.</summary>
    public static void SetGlobalFontSize(double pt)
    {
        _globalFontSize = Math.Clamp(pt, 8.0, 32.0);

        // Snapshot the live controls under the lock (pruning collected ones), then invoke outside it so a
        // handler can't re-enter the tracker lock.
        List<TerminalControl> live = new();
        lock (_fontTrackersGate)
        {
            _fontTrackers.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _fontTrackers)
                if (w.TryGetTarget(out var c)) live.Add(c);
        }
        foreach (var c in live) c.ApplyFontMetrics();
    }

    /// <summary>Cache of colour → brush so we don't allocate a brush per cell per repaint.</summary>
    private readonly Dictionary<uint, IBrush> _brushCache = new();

    /// <summary>Observable row strings bound by the ItemsControl in XAML.</summary>
    public AvaloniaList<string> Rows => _rows;

    /// <summary>
    /// Flattened text of all rows joined with newlines.
    /// Useful for assertions: check that a known string appears anywhere in the rendered output.
    /// </summary>
    public string RenderedText => string.Join("\n", _rows);

    /// <summary>
    /// Test seam: total number of full-transcript rebuilds performed since construction. Used to assert
    /// that a burst of PTY output chunks COALESCES into a bounded number of rebuilds rather than one
    /// rebuild per chunk (the per-chunk rebuild is what livelocked the UI thread and froze the cockpit).
    /// </summary>
    internal int RebuildCount { get; private set; }

    /// <summary>Test seam: the grid width (columns) the engine — and thus the PTY — is currently sized to.</summary>
    internal int PtyCols => _terminal.Cols;

    /// <summary>Test seam: the grid height (rows) the engine — and thus the PTY — is currently sized to.</summary>
    internal int PtyRows => _terminal.Rows;

    /// <summary>Test seam: whether the engine is on the alternate (full-screen TUI) buffer.</summary>
    internal bool IsAltBuffer => _terminal.IsAlternateBufferActive;

    /// <summary>
    /// Test seam: the CURRENT VT live-screen as exactly <see cref="PtyRows"/> rows of exactly
    /// <see cref="PtyCols"/> characters — a faithful, right-padded snapshot of the on-screen cell grid.
    /// This is the ground truth an in-place erase/redraw (CUP/EL/ED) produces; asserting the rendered
    /// output equals it is how a test proves there are NO ghost cells left under a partial redraw.
    /// </summary>
    internal IReadOnlyList<string> ScreenGrid()
    {
        TerminalBuffer buffer = _terminal.Buffer;
        int cols = _terminal.Cols;
        var grid = new List<string>(_terminal.Rows);
        for (int y = 0; y < _terminal.Rows; y++)
        {
            BufferLine? line = SafeLine(buffer, buffer.YBase + y);
            // TranslateToString(false, 0, cols) gives every cell (no right-trim) so a cell an erase
            // BLANKED reads back as a space — exactly what "no ghost survived" must look like.
            string s = line is null ? string.Empty : line.TranslateToString(false, 0, cols);
            if (s.Length < cols) s = s.PadRight(cols);
            else if (s.Length > cols) s = s.Substring(0, cols);
            grid.Add(s);
        }
        return grid;
    }

    public TerminalControl()
    {
        InitializeComponent();

        // A terminal must be focusable to receive keyboard input at all.
        Focusable = true;

        _terminal = BuildEngine(80, 24);

        // Initialize rows to match the terminal's initial size.
        RebuildRows();

        // Adopt the current app-wide font size (× the default zoom) and track future changes. Tracking is
        // WEAK (see TrackGlobalFontSize) so this control can still be collected if it's orphaned without a
        // detach ever firing — never a static-event leak.
        ApplyFontMetrics();
        TrackGlobalFontSize(this);

        // Start Avalonia size tracking.
        SizeChanged += OnSizeChanged;

        // Track scroll position so output follows the tail only while the operator is at the bottom.
        ScrollArea.ScrollChanged += OnScrollChanged;

        // Register for both Tunnel and Bubble strategies:
        // - Tunnel: captures keys as they route from the visual root down through TerminalControl,
        //   intercepting them before any child controls can handle them (correct production behavior).
        // - Bubble: also catches direct RaiseEvent calls (used in headless unit tests where the
        //   headless platform's keyboard routing produces a Bubble-only pass on the source control).
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // Mouse wheel: scroll our scrollback (Ctrl+wheel zooms). Tunnel so we get it before the inner
        // ScrollViewer and drive the real offset ourselves, marking it Handled to avoid a double-scroll.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>Scrollback depth (rows) the VT engine retains — the source-of-truth for a fresh engine.</summary>
    private const int ScrollbackLines = 1000;

    /// <summary>Builds a fresh XTerm VT engine of the given grid size, wired to forward device replies to the PTY.</summary>
    private XTerm.Terminal BuildEngine(int cols, int rows)
    {
        var engine = new XTerm.Terminal(new TerminalOptions
        {
            Cols = cols,
            Rows = rows,
            Scrollback = ScrollbackLines,
        });
        // When XTerm produces data (e.g. terminal query responses), forward it to the PTY.
        engine.DataReceived += OnTerminalDataReceived;
        return engine;
    }

    /// <summary>
    /// Replaces the VT engine with a fresh one of the SAME grid size so the next <see cref="Attach"/> replay
    /// reconstructs the CURRENT screen from an EMPTY buffer. A recycled control (dock tab switched away then
    /// back) keeps its old engine buffer; without this, Attach's full-backlog replay would write ON TOP of
    /// that surviving buffer and double every line. A fresh engine — not <c>Terminal.Reset()</c>, which
    /// clears cells but LEAVES the scrollback accumulator (YBase), so replay would append below stale blank
    /// rows — is what makes Attach idempotent w.r.t. whatever the engine held before.
    /// </summary>
    private void ResetEngine()
    {
        _terminal.DataReceived -= OnTerminalDataReceived;   // stop the discarded engine forwarding to the PTY
        _terminal = BuildEngine(_terminal.Cols, _terminal.Rows);
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
        // Re-attach safety: start from a CLEAN engine so the backlog replay below reconstructs the current
        // screen instead of doubling a recycled control's surviving buffer (see ResetEngine). Harmless on a
        // first attach — the engine is already empty, this just swaps in an equivalent one of the same size.
        ResetEngine();
        // Subscribing REPLAYS the session backlog synchronously (see PortaPtySession.Output.add) so this fresh
        // VT engine rebuilds the CURRENT screen from history. That replay re-parses every device-status query
        // the child ever emitted (ESC[6n / ESC[?6n / ESC[c), and XTerm dutifully re-answers each — but those
        // answers describe STALE history, and writing them back injects "[1;6R"-style reports into the child
        // that is now sitting at an interactive prompt, where they surface as garbage in what the user types.
        // So bracket the replay: answers generated while _replaying are dropped (see OnTerminalDataReceived);
        // only LIVE queries — output that arrives after Attach returns — are answered. Rebuilding screen state
        // is unaffected: _terminal.Write still runs; only the write-BACK to the child is suppressed.
        _replaying = true;
        try { session.Output += OnSessionOutput; }
        finally { _replaying = false; }
        session.Exited += OnSessionExited;
        _humanComposing = false;   // a fresh session starts with no line half-typed
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
        UntrackGlobalFontSize(this);   // best-effort prompt cleanup; the weak ref already makes us collectable
        base.OnDetachedFromVisualTree(e);
    }

    // ── Session event handlers (called on background thread) ────────────────

    private void OnSessionOutput(string text)
    {
        // Write to XTerm engine — Terminal.Write is safe to call from any thread. This is the VT-state
        // update and MUST stay eager and in-order; only the (expensive) rebuild is deferred/coalesced.
        _terminal.Write(text);

        // Coalesce the buffer rebuild. A streaming agent (its startup banner, a TUI redraw) fires Output
        // in a rapid burst; rebuilding the full transcript once PER CHUNK let the render queue outpace its
        // own drain — a CPU-bound UI-thread livelock that pinned a core at 100% and froze the whole cockpit
        // (issue: terminal-pane livelocks the UI thread). Instead we schedule at most ONE rebuild in flight,
        // so a burst collapses into a single rebuild bounded by the render clock, not the input rate.
        ScheduleRebuild();
    }

    // ── Coalesced rebuild scheduling ─────────────────────────────────────────
    // _renderDirty / _rebuildScheduled are touched from BOTH the background PTY thread (via
    // OnSessionOutput) and the UI thread (the posted rebuild), so all access is under _rebuildGate.
    private readonly object _rebuildGate = new();
    private bool _renderDirty;
    private bool _rebuildScheduled;

    /// <summary>
    /// True only while <see cref="Attach"/> is replaying the session backlog into the VT engine. During that
    /// synchronous window the engine re-answers device-status queries embedded in the replayed history; those
    /// answers are stale and must NOT be written back to the child (see <see cref="OnTerminalDataReceived"/>).
    /// Volatile because live output — which reads it (as false) on the background PTY thread — races the
    /// UI-thread replay that sets/clears it.
    /// </summary>
    private volatile bool _replaying;

    /// <summary>
    /// True while the operator is composing a line in this pane — from the first keystroke of a line until
    /// they submit it (Enter/CR) or abort it (Ctrl+C ETX / Ctrl+D EOT). While composing, the VT engine's
    /// device-query auto-answers (cursor-position reports and friends) are NOT written back to the child:
    /// the child polls the cursor on a render timer, and a CSI…R report injected into stdin BETWEEN the
    /// operator's keystrokes lands in the child's line editor as literal text — the "so w[?17;80Re ju…"
    /// corruption of the typed message. This is the LIVE analog of the replayed-answer leak <see cref="_replaying"/>
    /// suppresses (fixed in 814a087); the child re-queries once the line is submitted. Volatile because it's
    /// set/cleared on the UI thread (key/text input) and read on the background PTY thread (in
    /// <see cref="OnTerminalDataReceived"/>).
    /// </summary>
    private volatile bool _humanComposing;

    // ── Virtualized render state ─────────────────────────────────────────────
    /// <summary>Rows rendered above and below the viewport so a small scroll doesn't flash blank.</summary>
    private const int OverscanRows = 8;
    /// <summary>Full transcript height in rows (drives the scroll surface extent); the plain <see cref="_rows"/> hold their text.</summary>
    private int _rowCount;
    /// <summary>Absolute buffer row of the cursor, captured at the last rebuild (for the block-cursor draw).</summary>
    private int _cursorAbsRow;
    /// <summary>The [first,last) transcript slice currently built into inlines — a scroll that doesn't move it skips the rebuild.</summary>
    private int _renderedFirstRow;
    private int _renderedLastRow = -1;

    /// <summary>
    /// Pixels of blank padding above the first transcript row so the terminal is BOTTOM-ANCHORED: when the
    /// content is shorter than the viewport, the last row rests on the bottom edge and the short buffer pads
    /// at the TOP (like a real terminal), instead of floating in the middle/top of the pane. Zero once the
    /// transcript is tall enough to fill (and overflow) the viewport.
    /// </summary>
    private double _topPad;

    /// <summary>
    /// Marks the rendered view dirty and ensures exactly one rebuild is queued on the UI thread. A burst of
    /// output that arrives before the queued rebuild runs collapses into that single rebuild. Safe to call
    /// from any thread (<see cref="IPtySession.Output"/> fires on a background PTY thread).
    /// </summary>
    private void ScheduleRebuild()
    {
        lock (_rebuildGate)
        {
            _renderDirty = true;
            if (_rebuildScheduled) return;   // a rebuild is already queued — it will pick up this output too
            _rebuildScheduled = true;
        }
        Dispatcher.UIThread.Post(RunCoalescedRebuild, DispatcherPriority.Render);
    }

    /// <summary>
    /// The single queued rebuild: renders the current buffer once, then (if following the tail) scrolls to
    /// the end AFTER layout has taken in the new content so the live prompt/last line stays visible. Output
    /// that arrived after the last clear-of-dirty re-arms a fresh rebuild via <see cref="ScheduleRebuild"/>.
    /// </summary>
    private void RunCoalescedRebuild()
    {
        lock (_rebuildGate)
        {
            _rebuildScheduled = false;
            if (!_renderDirty) return;
            _renderDirty = false;
        }
        RebuildRows();
        if (_followTail)
            Dispatcher.UIThread.Post(ScrollToTail, DispatcherPriority.Loaded);
    }

    /// <summary>Pins the viewport to the bottom of the transcript (keeps the live prompt/last line in view).</summary>
    private void ScrollToTail()
    {
        double max = ScrollArea.Extent.Height - ScrollArea.Viewport.Height;
        if (max > 0) ScrollArea.Offset = ScrollArea.Offset.WithY(max);
    }

    /// <summary>
    /// Recomputes <see cref="_followTail"/> from the scroll position: following iff the viewport is at (or
    /// within a line of) the bottom. This makes output auto-follow the tail until the operator scrolls up,
    /// and resume following once they scroll back down — with no need to distinguish user vs programmatic
    /// scrolls (a programmatic ScrollToEnd simply leaves us following).
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        double max = ScrollArea.Extent.Height - ScrollArea.Viewport.Height;
        _followTail = ScrollArea.Offset.Y >= max - _cellH;   // within one row of the bottom counts as "at bottom"

        // Virtualize on scroll: render the rows that just came into view. Re-rendering the slice changes only
        // the inlines and the text block's Canvas.Top — not the surface extent or the scroll offset — so this
        // never re-enters OnScrollChanged, and the [first,last) guard makes an unchanged window a no-op.
        RenderVisibleSlice();
    }

    private void OnSessionExited()
    {
        // No UI action needed beyond potential future status display.
    }

    // ── XTerm → PTY data (terminal query responses) ─────────────────────────

    private void OnTerminalDataReceived(object? sender, TerminalEvents.DataEventArgs e)
    {
        // Replayed history is read-only: a device-status answer the engine generates while Attach is replaying
        // the backlog describes a stale frame, and injecting it into the child (now at a prompt) is the
        // "[1;6R / [?1;6R garbage" corruption. Drop it — only LIVE queries get answered. See Attach().
        if (_replaying) return;

        // While the operator is composing a line, the child's live cursor-position polls must go UNANSWERED:
        // writing a CSI…R report into stdin between the operator's keystrokes corrupts the typed line (the
        // "so w[?17;80Re ju…" leak — the live analog of the replay bug above). The child re-queries once the
        // line is submitted, which clears _humanComposing. See NoteHumanInput().
        if (_humanComposing) return;

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
        NoteHumanInput(vtSequence);   // opens/closes the compose window that gates device-query answers
        FireAndForgetWrite(vtSequence);
    }

    /// <summary>
    /// Raised whenever the user interacts with this terminal (key down, text input, or pointer press).
    /// The hosting view subscribes to forward the interaction to the pane's attention monitor.
    /// </summary>
    public event EventHandler? UserInteracted;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        UserInteracted?.Invoke(this, EventArgs.Empty);
        if (_session is null) return;

        string? vtSequence = TranslateKey(e);
        if (vtSequence is null) return;

        e.Handled = true;
        NoteHumanInput(vtSequence);   // opens/closes the compose window that gates device-query answers
        // Fix 1: route through shared fire-and-forget helper so exceptions are never silently lost.
        FireAndForgetWrite(vtSequence);
    }

    /// <summary>Clicking the terminal focuses it so it receives keyboard input.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        UserInteracted?.Invoke(this, EventArgs.Empty);
        Focus();
        base.OnPointerPressed(e);
    }

    /// <summary>
    /// Mouse wheel: Ctrl+wheel zooms; otherwise scrolls our scrollback. Registered on the Tunnel route so
    /// this fires before the inner ScrollViewer, letting us drive the real offset (and mark Handled to avoid
    /// a double scroll). On the alternate buffer the running app owns scrolling, so we don't hijack it.
    /// </summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Delta.Y > 0) ZoomIn();
            else if (e.Delta.Y < 0) ZoomOut();
            e.Handled = true;
            return;
        }
        if (HandleWheelScroll(e.Delta.Y)) e.Handled = true;
    }

    /// <summary>
    /// Scrolls the transcript by <paramref name="deltaY"/> wheel notches (wheel-up = positive = scroll BACK
    /// through scrollback). Faithful to the VT buffer: it moves the real ScrollViewer offset over the
    /// virtualized transcript, so returning to the bottom resumes tail-following (see OnScrollChanged).
    /// Returns false (a no-op) on the alternate buffer — a full-screen TUI manages its own scroll — or when
    /// there's nothing to scroll. Internal so a headless test can drive it without synthesizing pointer input.
    /// </summary>
    internal bool HandleWheelScroll(double deltaY)
    {
        if (_terminal.IsAlternateBufferActive) return false;
        double max = Math.Max(0, ScrollArea.Extent.Height - ScrollArea.Viewport.Height);
        if (max <= 0) return false;
        const double rowsPerNotch = 3;
        double newY = Math.Clamp(ScrollArea.Offset.Y - deltaY * rowsPerNotch * _cellH, 0, max);
        ScrollArea.Offset = ScrollArea.Offset.WithY(newY);
        return true;
    }

    /// <summary>
    /// Printable text input — letters, DIGITS, symbols, respecting keyboard layout and shift —
    /// is forwarded straight to the PTY. Control/navigation keys go through OnKeyDown instead,
    /// so nothing is double-sent. This is what makes ordinary typing (e.g. answering "1") work.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        UserInteracted?.Invoke(this, EventArgs.Empty);
        if (_session is not null && !string.IsNullOrEmpty(e.Text))
        {
            NoteHumanInput(e.Text);   // printable text opens the compose window that gates device-query answers
            FireAndForgetWrite(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    /// <summary>
    /// Updates <see cref="_humanComposing"/> from a piece of input the OPERATOR just produced (a keystroke or
    /// printable text). Ordinary input opens the compose window; a line terminator (Enter/CR/LF) or an abort
    /// (Ctrl+C ETX / Ctrl+D EOT) closes it, so device-query answers flow again once the line leaves the editor.
    /// Called ONLY from the human input paths — never from <see cref="OnTerminalDataReceived"/> — so a device
    /// report can never be mistaken for typing.
    /// </summary>
    private void NoteHumanInput(string? vtSequence)
    {
        if (string.IsNullOrEmpty(vtSequence)) return;
        _humanComposing = !(vtSequence.Contains('\r') || vtSequence.Contains('\n') ||
                            vtSequence.Contains('\x03') || vtSequence.Contains('\x04'));
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

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => RefitGrid(e.NewSize);

    /// <summary>
    /// Re-fits the terminal grid (cols/rows) to <paramref name="size"/> using the MEASURED monospace cell,
    /// minus the ScrollViewer padding — so the PTY grid matches exactly what is rendered and claude's TUI
    /// neither wraps early nor overflows. Re-measures first in case the font system came up after construction.
    /// </summary>
    private void RefitGrid(Size size)
    {
        MeasureCell();

        int cols = Math.Max(10, (int)((size.Width - PadX) / _cellW));
        int rows = Math.Max(4, (int)((size.Height - PadY) / _cellH));

        // Publish the real grid so the NEXT agent spawns at this width (not a hardcoded default) and its
        // banner isn't drawn wide then reflowed narrow.
        Styloagent.Core.Sessions.AgentSession.SetInitialGrid(cols, rows);

        // Capture whether the view was following the tail BEFORE the relayout. A window resize or a zoom (which
        // routes here via ApplyFontMetrics) changes the ScrollViewer's extent/viewport but PRESERVES the old
        // scroll offset — so a view that was pinned to the bottom loses the agent's newest lines. Worse, the
        // extent/viewport change fires OnScrollChanged, which recomputes _followTail from the now-stale offset
        // and clears it; so we must decide off the PRE-relayout follow state, not the post-relayout one.
        bool wasFollowing = _followTail;

        if (cols != _terminal.Cols || rows != _terminal.Rows)
        {
            _terminal.Resize(cols, rows);
            _session?.Resize(cols, rows);
            RebuildRows();
        }

        // Re-pin to the tail AFTER layout absorbs the new extent/viewport (Loaded runs post-layout, same as the
        // coalesced-rebuild tail-follow), so a resize/zoom that was following keeps the live prompt in view.
        // Only when we were already at the bottom — a deliberately scrolled-up operator is left untouched.
        if (wasFollowing)
            Dispatcher.UIThread.Post(ScrollToTail, DispatcherPriority.Loaded);
    }

    // ── Buffer render ────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the rendered screen. Keeps the plain-text <see cref="Rows"/> (for RenderedText /
    /// assertions) and rebuilds the coloured inline runs shown in the SelectableTextBlock.
    /// Must be called on the UI thread.
    /// </summary>
    private void RebuildRows()
    {
        RebuildCount++;
        TerminalBuffer buffer = _terminal.Buffer;

        // A full-screen TUI (btop, vim, less) runs on the ALTERNATE buffer: a fixed Rows-tall canvas with
        // NO scrollback, painted by absolute cursor addressing and in-place partial redraws. It MUST be
        // rendered as the exact Rows-tall screen grid — never trimmed, never mixed with scrollback. Trimming
        // its (often blank) bottom rows makes the rendered height jitter every frame, so rows re-anchor each
        // repaint and stale glyphs from the previous frame's taller render survive underneath → the "garbled
        // btop / overlapping redraw" corruption. So: on the alt buffer, render exactly [YBase, YBase+Rows).
        bool altBuffer = _terminal.IsAlternateBufferActive;

        int total = buffer.YBase + _terminal.Rows;
        int cursorAbsRow = buffer.YBase + buffer.Y;
        int count;
        if (altBuffer)
        {
            // Alt buffer: the whole screen is live and meaningful (a blank row inside btop is a real blank
            // row, not trailing filler). Render every row of the Rows-tall grid, no trailing-blank trim.
            count = total;
        }
        else
        {
            // Normal buffer: render the FULL transcript — scrollback + the live screen — so the ScrollViewer
            // can scroll the whole VT buffer and no earlier output is clipped. The live screen is usually
            // mostly blank below the cursor, so trim trailing blank rows (but never above the cursor) — that
            // way ScrollToEnd lands on the prompt, not on empty space below it.
            int lastRow = cursorAbsRow;
            for (int r = total - 1; r > lastRow; r--)
            {
                BufferLine? l = SafeLine(buffer, r);
                if (l is not null && l.GetTrimmedLength() > 0) { lastRow = r; break; }
            }
            count = Math.Max(1, lastRow + 1);
        }

        // Plain-text rows — kept for RenderedText and test assertions. Cheap (strings only), so the FULL
        // transcript stays here for scrollback search/copy even though the coloured render is virtualized.
        while (_rows.Count < count) _rows.Add(string.Empty);
        while (_rows.Count > count) _rows.RemoveAt(_rows.Count - 1);
        for (int r = 0; r < count; r++)
        {
            BufferLine? l = SafeLine(buffer, r);
            _rows[r] = l is null ? string.Empty : l.TranslateToString(true, 0, l.Length);
        }

        _rowCount = count;
        _cursorAbsRow = cursorAbsRow;

        // BOTTOM-ANCHOR: a terminal rests its last row on the bottom edge — output scrolls UP off the top and
        // a short buffer pads at the TOP, never floating in the middle/top of the pane. When the transcript
        // is shorter than the viewport, push it down by the leftover space so its last row hugs the bottom.
        double contentH = count * _cellH;
        double vpH = ScrollArea.Viewport.Height;
        _topPad = vpH > 0 && contentH < vpH ? vpH - contentH : 0.0;

        // Size the scroll surface to the WHOLE transcript so the scrollbar spans it, then render only the
        // rows in view. Building inlines for every row (Clear + a Run per colour span across up to ~1000
        // scrollback rows) is what pinned the UI thread at 100% CPU on a layout switch — virtualizing to the
        // viewport makes each rebuild O(visible rows). The surface is at least the viewport tall (so the
        // bottom-anchor top-pad has room and the last row can sit on the bottom edge).
        Surface.Height = Math.Max(contentH + _topPad, vpH);
        Surface.Width  = Math.Max(_terminal.Cols * _cellW + PadX, ScrollArea.Viewport.Width);
        RenderVisibleSlice(forceRebuild: true);
    }

    /// <summary>
    /// Renders ONLY the transcript rows intersecting the current viewport (plus a small over-scan) into
    /// <c>ScreenText.Inlines</c>, positioning the text block within the full-height surface via Canvas.Top so
    /// row <c>r</c> lands at its absolute <c>r · cellH</c>. Called on every rebuild and on scroll; a scroll
    /// that doesn't move the visible slice is a no-op unless <paramref name="forceRebuild"/> is set (the
    /// buffer content changed). This is the crux of the fix — a rebuild is O(viewport), never O(transcript).
    /// </summary>
    private void RenderVisibleSlice(bool forceRebuild = false)
    {
        int count = _rowCount;
        if (count <= 0) return;

        double vpTop = ScrollArea.Offset.Y;
        double vpH   = ScrollArea.Viewport.Height;

        // Rows are laid out starting at _topPad (the bottom-anchor offset), so map the scroll position back
        // through that pad before turning it into a row index.
        int first, last;
        if (vpH > 0)
        {
            first = Math.Max(0, (int)((vpTop - _topPad) / _cellH) - OverscanRows);
            int visRows = (int)Math.Ceiling(vpH / _cellH) + 2 * OverscanRows;
            last = Math.Min(count, first + visRows);
        }
        else
        {
            // Not laid out yet (before the first measure): render a BOUNDED tail window, never the whole
            // transcript, so we can't storm even pre-layout.
            int window = _terminal.Rows + 2 * OverscanRows;
            first = Math.Max(0, count - window);
            last = count;
        }

        if (!forceRebuild && first == _renderedFirstRow && last == _renderedLastRow) return;
        _renderedFirstRow = first;
        _renderedLastRow = last;

        // Row `first` lands at _topPad + first·cellH so the transcript is bottom-anchored (short buffers pad
        // at the top and the last row rests on the bottom edge).
        Canvas.SetTop(ScreenText, _topPad + first * _cellH);
        BuildColoredInlines(first, last, _cursorAbsRow);
    }

    /// <summary>
    /// Walks the visible XTerm cell grid and rebuilds <c>ScreenText.Inlines</c> as coloured
    /// <see cref="Run"/> spans — consecutive cells sharing the same foreground, background and
    /// weight become one Run. Handles per-cell background, inverse video (fg/bg swapped) and bold.
    /// Falls back gracefully (default colours) if the buffer can't be read.
    /// </summary>
    private void BuildColoredInlines(int first, int last, int cursorAbsRow)
    {
        var inlines = ScreenText.Inlines;
        if (inlines is null)
        {
            inlines = new InlineCollection();
            ScreenText.Inlines = inlines;
        }
        inlines.Clear();

        TerminalBuffer buffer = _terminal.Buffer;

        // Cursor position, drawn as an inverse block so the caret tracks the input point. XTerm.NET
        // treats the cursor as a renderer concern (it is NOT composited into the buffer cells), so if
        // we don't draw it here the terminal shows no moving caret — it looks "stuck". Because we now
        // render the full transcript, the cursor's row is its ABSOLUTE buffer row (YBase + buffer.Y).
        bool cursorVisible = _terminal.CursorVisible;
        int cursorCol = buffer.X;

        var runText = new StringBuilder();

        for (int row = first; row < last; row++)
        {
            BufferLine? line = SafeLine(buffer, row);
            int cols = line?.Length ?? 0;
            bool rowHasCursor = cursorVisible && row == cursorAbsRow && cursorCol >= 0 && cursorCol < cols;

            // Trim only trailing cells that are BOTH blank AND default-background — a trailing run
            // of coloured-background cells is a visible block and must be kept.
            int lastVisible = -1;
            for (int col = cols - 1; col >= 0; col--)
            {
                BufferCell c = line![col];
                (_, uint bg, _) = ResolveCell(c.Attributes);
                bool blank = string.IsNullOrEmpty(c.Content) || c.Content == " ";
                if (!blank || bg != _defaultBg) { lastVisible = col; break; }
            }
            // Keep the cursor cell even when it sits on a trailing blank — typing at end-of-line is
            // the common case, and trimming it away is exactly what makes the caret disappear/stick.
            if (rowHasCursor) lastVisible = Math.Max(lastVisible, cursorCol);

            runText.Clear();
            uint runFg = _defaultFg, runBg = _defaultBg;
            bool runBold = false, runOpen = false;

            for (int col = 0; col <= lastVisible; col++)
            {
                BufferCell cell = line![col];
                if (cell.Width == 0) continue; // wide-char continuation cell — skip

                string content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
                (uint fg, uint bg, bool bold) = ResolveCell(cell.Attributes);

                // The cursor cell is drawn inverse (block cursor): swap fg/bg so it renders as a solid
                // block with the character showing through. This naturally breaks it into its own run.
                if (rowHasCursor && col == cursorCol) (fg, bg) = (bg, fg);

                if (!runOpen)
                {
                    (runFg, runBg, runBold, runOpen) = (fg, bg, bold, true);
                }
                else if (fg != runFg || bg != runBg || bold != runBold)
                {
                    FlushRun(inlines, runText, runFg, runBg, runBold);
                    (runFg, runBg, runBold) = (fg, bg, bold);
                }

                runText.Append(content);
            }

            if (runOpen) FlushRun(inlines, runText, runFg, runBg, runBold);

            // Newline separator between rendered rows (not after the last row of the slice).
            if (row < last - 1)
                inlines.Add(new Run("\n"));
        }
    }

    private void FlushRun(InlineCollection inlines, StringBuilder text, uint fg, uint bg, bool bold)
    {
        if (text.Length == 0) return;
        var run = new Run(text.ToString()) { Foreground = BrushFor(fg) };
        // Only paint a background when it differs from the terminal default (keeps the control's
        // own dark background showing through for ordinary cells).
        if (bg != _defaultBg) run.Background = BrushFor(bg);
        if (bold) run.FontWeight = FontWeight.Bold;
        inlines.Add(run);
        text.Clear();
    }

    private static BufferLine? SafeLine(TerminalBuffer buffer, int index)
    {
        try { return buffer.Lines[index]; }
        catch { return null; }
    }

    /// <summary>
    /// Resolves an XTerm cell to (foreground, background, bold), applying inverse video by swapping
    /// foreground and background.
    /// </summary>
    private (uint fg, uint bg, bool bold) ResolveCell(AttributeData attr)
    {
        uint fg = ResolveColor(attr.GetFgColorMode(), attr.GetFgColor(), isForeground: true);
        uint bg = ResolveColor(attr.GetBgColorMode(), attr.GetBgColor(), isForeground: false);
        if (attr.IsInverse()) (fg, bg) = (bg, fg);
        return (fg, bg, attr.IsBold());
    }

    /// <summary>
    /// Resolves an XTerm colour to packed 0xAARRGGBB. Mode 1 = 24-bit truecolor (packed RGB);
    /// mode 0 with index 0-255 = palette; 256 = default fg, 257 = default bg → the matching default.
    /// </summary>
    private uint ResolveColor(int mode, int c, bool isForeground)
    {
        if (mode == 1) return 0xFF000000u | (uint)(c & 0xFFFFFF);
        if (c >= 0 && c <= 255) return Palette256[c];
        return isForeground ? _defaultFg : _defaultBg;
    }

    /// <summary>
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
