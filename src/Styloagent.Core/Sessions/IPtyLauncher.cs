namespace Styloagent.Core.Sessions;

public interface IPtyLauncher
{
    Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default);
}
