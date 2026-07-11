namespace Styloagent.Core.Issues;

/// <summary>
/// An issue an agent encountered, dropped into <c>.styloagent/issues/</c>. Internal now; the
/// <see cref="Source"/>/<see cref="Status"/> fields leave room for an external GitHub feed handled by
/// a future triage agent (Source = "github", Status flowing open → triaged → closed).
/// </summary>
public sealed record Issue(
    string Id,
    string Title,
    string Detail,
    string Reporter,
    DateTimeOffset Timestamp,
    string Severity,   // low | medium | high
    string Status,     // open | triaged | closed
    string Source);    // internal | github
