namespace Styloagent.Core.Browser;

public enum BrowserRunMode { Observe, Test, Operate }
public enum BrowserJobStatus { Pending, Approved, Running, Completed, Failed, Cancelled }

/// <summary>
/// A declarative, durable browser request. It contains credential references only—never credential values.
/// The approved target URI is resolved from the environment registry rather than supplied as an arbitrary URL.
/// </summary>
public sealed record BrowserJob(
    string Id,
    string Requester,
    string EnvironmentId,
    BrowserRunMode Mode,
    string Purpose,
    string RelativePath,
    string? Selector,
    bool FullPage,
    string? CredentialRef,
    BrowserJobStatus Status,
    string? Approver,
    string? ArtifactPath,
    string? Failure,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt);

public sealed record BrowserOperationResult(bool Success, string Message, BrowserJob? Job = null)
{
    public static BrowserOperationResult Ok(string message, BrowserJob? job = null) => new(true, message, job);
    public static BrowserOperationResult Fail(string message, BrowserJob? job = null) => new(false, message, job);
}

public sealed record BrowserRunResult(bool Success, string? ArtifactPath, string? Failure)
{
    public static BrowserRunResult Completed(string artifactPath) => new(true, artifactPath, null);
    public static BrowserRunResult Failed(string failure) => new(false, null, failure);
}
