namespace Styloagent.Core.Router;

/// <summary>A mutation the coordinator should apply to the ledger.</summary>
public enum RouterAction { Grant, Expire }

/// <summary>One resolver decision: grant a claim (with a lease <see cref="Expires"/>) or expire a grant.</summary>
public sealed record RouterDecision(
    RouterAction Action, string Env, ResourceKind Kind, string Name, string Prefix, DateTimeOffset? Expires);
