namespace Styloagent.Core.Sessions;

public sealed record PtySpawnOptions(
    string Command,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Env,
    int Cols,
    int Rows);
