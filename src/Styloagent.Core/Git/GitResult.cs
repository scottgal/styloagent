namespace Styloagent.Core.Git;

/// <summary>Result of a git operation that returns no data. Never throws across the seam.</summary>
public sealed record GitResult(bool Ok, string? Error)
{
    public static GitResult Success() => new(true, null);
    public static GitResult Fail(string error) => new(false, error);
}

/// <summary>Result of a git operation that returns a value on success.</summary>
public sealed record GitResult<T>(bool Ok, T? Value, string? Error)
{
    public static GitResult<T> Success(T value) => new(true, value, null);
    public static GitResult<T> Fail(string error) => new(false, default, error);
}
