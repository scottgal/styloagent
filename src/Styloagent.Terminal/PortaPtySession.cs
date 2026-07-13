using System.Text;
using Porta.Pty;
using Styloagent.Core.Sessions;

namespace Styloagent.Terminal;

/// <summary>
/// Wraps a Porta.Pty <see cref="IPtyConnection"/> and exposes it as <see cref="IPtySession"/>.
/// </summary>
public sealed class PortaPtySession : IPtySession
{
    private readonly IPtyConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    // Torn-read-safe: updated via Volatile.Write in the read loop, read via Volatile.Read in IsIdle.
    private long _lastOutputTicks = DateTime.UtcNow.Ticks;

    // Output arrives the instant the PTY spawns (claude's banner), but the terminal view subscribes a beat
    // later (PtyStarted → Dispatcher.Post → Attach). A plain event drops everything emitted before that first
    // subscribe — the "missing letters at the top of the banner" race. So we buffer output and replay the
    // backlog to each new subscriber, under a lock, so no byte is lost or duplicated regardless of timing.
    private readonly object _outputGate = new();
    private readonly StringBuilder _backlog = new();
    private Action<string>? _output;

    // The backlog only bridges the spawn→attach gap (and re-attaches). Cap it so a long-lived, chatty agent
    // can't grow it without bound; the XTerm engine keeps its own scrollback for history.
    private const int BacklogCap = 256 * 1024;

    /// <summary>
    /// Raised when the PTY process writes output. On subscribe, the accumulated backlog is replayed to the
    /// new handler first (so a late subscriber still sees the banner), then live output follows.
    /// </summary>
    /// <remarks>
    /// Raised on a background thread. Consumers must marshal to their own synchronization context
    /// (e.g. the UI thread) before touching UI state.
    /// </remarks>
    public event Action<string>? Output
    {
        add
        {
            lock (_outputGate)
            {
                if (value is not null && _backlog.Length > 0) value(_backlog.ToString());
                _output += value;
            }
        }
        remove
        {
            lock (_outputGate) { _output -= value; }
        }
    }

    /// <summary>
    /// Raised when the PTY process exits.
    /// </summary>
    /// <remarks>
    /// Raised on a background thread. Consumers must marshal to their own synchronization context
    /// (e.g. the UI thread) before touching UI state.
    /// </remarks>
    public event Action? Exited;

    public bool IsIdle => (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastOutputTicks)) > TimeSpan.FromMilliseconds(250).Ticks;

    internal PortaPtySession(IPtyConnection connection)
    {
        _connection = connection;
        _connection.ProcessExited += OnProcessExited;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the PTY's input stream.
    /// </summary>
    /// <remarks>
    /// The synchronous write itself is best-effort and cannot be interrupted mid-write;
    /// <paramref name="ct"/> is checked before the write begins but not during it.
    /// <para>
    /// <b>WriteAsync is NOT safe for concurrent callers.</b> Callers must serialize writes.
    /// The sole consumer <c>AgentSession</c> already awaits each write sequentially, satisfying this requirement.
    /// </para>
    /// </remarks>
    public ValueTask WriteAsync(string text, CancellationToken ct = default)
    {
        // Porta.Pty's PtyStream on macOS/Unix is backed by a raw fd.
        // FlushAsync hangs and synchronous Flush() deadlocks when called from an async continuation
        // that competes with the background ReadAsync on the same stream fd.
        // Running the write synchronously on a thread-pool thread avoids these issues.
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ValueTask(Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _connection.WriterStream.Write(bytes, 0, bytes.Length);
            _connection.WriterStream.Flush();
        }, ct));
    }

    public void Resize(int cols, int rows) => _connection.Resize(cols, rows);

    public async ValueTask DisposeAsync()
    {
        _connection.ProcessExited -= OnProcessExited;

        // Kill the process FIRST so the blocking ReaderStream.ReadAsync syscall returns.
        // Only then cancel + wait for the read loop; otherwise CancellationToken alone
        // cannot unblock a native read() call on a Unix PTY fd.
        // Note: killing before draining the read buffer may drop final unread output
        // (acceptable: the dehydrate ack has already fired by this point).
        _connection.Kill();
        await _cts.CancelAsync().ConfigureAwait(false);

        try { await _readLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* swallow read-loop errors on dispose */ }

        _cts.Dispose();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e) => Exited?.Invoke();

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            int bytesRead;
            try
            {
                bytesRead = await _connection.ReaderStream
                    .ReadAsync(buffer, 0, buffer.Length, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Stream closed (process exited) — stop reading
                break;
            }

            if (bytesRead <= 0) break;

            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Append to the replay backlog AND deliver live, under one lock: a subscriber that attaches
            // between these would otherwise either miss this chunk or get it twice.
            lock (_outputGate)
            {
                _backlog.Append(text);
                if (_backlog.Length > BacklogCap)
                    _backlog.Remove(0, _backlog.Length - BacklogCap);
                _output?.Invoke(text);
            }
        }
    }
}
