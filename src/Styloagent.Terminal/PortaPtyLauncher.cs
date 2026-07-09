using Porta.Pty;
using Styloagent.Core.Sessions;

namespace Styloagent.Terminal;

/// <summary>
/// Implements <see cref="IPtyLauncher"/> using Porta.Pty's <see cref="PtyProvider"/>.
/// </summary>
public sealed class PortaPtyLauncher : IPtyLauncher
{
    public async Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
    {
        var ptyOptions = new PtyOptions
        {
            App = options.Command,
            CommandLine = options.Args.ToArray(),
            // Defense in depth: Porta.Pty throws ArgumentNullException on an empty Cwd,
            // so never pass one through — resolve to a real, existing directory.
            Cwd = WorkingDirectoryResolver.Resolve(options.WorkingDirectory),
            Cols = options.Cols,
            Rows = options.Rows,
        };

        if (options.Env is { Count: > 0 })
        {
            ptyOptions.Environment = new Dictionary<string, string>(options.Env);
        }

        var connection = await PtyProvider.SpawnAsync(ptyOptions, ct).ConfigureAwait(false);
        return new PortaPtySession(connection);
    }
}
