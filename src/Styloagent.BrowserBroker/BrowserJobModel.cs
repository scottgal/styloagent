namespace Styloagent.BrowserBroker;

public enum BrowserRunMode { Observe, Test, Operate }
public enum BrowserJobStatus { Pending, Approved, Running, Completed, Failed, Cancelled }

/// <summary>
/// Optional login step for a governed run: the email and password are supplied as credential
/// <em>references</em> (env:VAR / keychain://ITEM / secret://NAME — never literal values), resolved by
/// the broker's credential provider at run time and never logged, persisted, or written to artifacts.
/// </summary>
public sealed record LoginStep(string EmailRef, string PasswordRef, string? SubmitSelector = null);

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
    LoginStep? Login,
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
