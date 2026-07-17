using Styloagent.Core.Ownership;

namespace Styloagent.Core.Hooks;

/// <summary>The outcome of an ownership gate check.</summary>
public enum GateOutcome { Allow, Deny }

/// <summary>
/// A gate decision: <see cref="GateOutcome.Allow"/> (no reason), or <see cref="GateOutcome.Deny"/> carrying
/// the operator-facing "prod" instruction the PreToolUse hook returns to the blocked agent.
/// </summary>
public sealed record GateDecision(GateOutcome Outcome, string? Reason)
{
    public static readonly GateDecision Allowed = new(GateOutcome.Allow, null);
    public static GateDecision Denied(string reason) => new(GateOutcome.Deny, reason);
    public bool IsAllowed => Outcome == GateOutcome.Allow;
}

/// <summary>
/// Slice 2 of the ownership-enforcement design: the PreToolUse gate policy. Composes the pure
/// <see cref="OwnershipMap"/> resolver with the escape-hatch rules so a cross-owner write is BLOCKED with a
/// prod message. Pure and never-throws, so a hook can call it synchronously to decide allow/deny.
/// </summary>
public sealed class OwnershipGate
{
    /// <summary>The coordination-root prefix that bypasses the gate (maintains the map).</summary>
    private const string Overview = "overview-";

    /// <summary>The write tools the gate governs — ownership gates WRITES only; reads are never gated.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "NotebookEdit",
    };

    private readonly OwnershipMap _map;
    private readonly string _repoRoot;

    public OwnershipGate(OwnershipMap map, string repoRoot)
    {
        _map = map;
        _repoRoot = (repoRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>
    /// Decide whether <paramref name="caller"/> may write <paramref name="path"/> via <paramref name="tool"/>.
    /// </summary>
    public GateDecision Decide(string? caller, string? tool, string? path)
    {
        try
        {
            // Writes only — a Read/Grep/Glob/Bash never touches ownership.
            if (tool is null || !WriteTools.Contains(tool)) return GateDecision.Allowed;

            // overview- is the coordination root and maintains the map — it bypasses the gate.
            if (caller == Overview) return GateDecision.Allowed;

            string rel = ToRepoRelative(path);

            // Never gate build output, tests, docs or runtime state — over-blocking these just gets in the way
            // and they're shared/gitignored anyway (§4). (Full .gitignore awareness is a transport-layer concern.)
            if (IsExempt(rel)) return GateDecision.Allowed;

            string? owner = _map.OwnerOf(rel);
            if (owner is not null && owner != caller)
                return GateDecision.Denied(
                    $"{rel} is owned by {owner}. Do not edit it. Coordinate: send_message overview- (or {owner}) " +
                    "to request a lease, or hand the change to the owner.");
            return GateDecision.Allowed;
        }
        catch
        {
            // §4 degrade-never-destroy: a gate crash must FAIL OPEN — never hard-block an agent (and wedge the
            // fleet) because the enforcement layer itself threw.
            return GateDecision.Allowed;
        }
    }

    /// <summary>
    /// Paths the gate must never block: top-level <c>tests/</c>, <c>docs/</c>, <c>.styloagent/</c>, and any
    /// <c>obj/</c> or <c>bin/</c> build-output segment anywhere in the tree (§4 escape hatches).
    /// </summary>
    private static bool IsExempt(string rel)
    {
        if (rel.Length == 0) return true;
        if (rel.StartsWith("tests/", StringComparison.Ordinal)
            || rel.StartsWith("docs/", StringComparison.Ordinal)
            || rel.StartsWith(".styloagent/", StringComparison.Ordinal))
            return true;
        foreach (string seg in rel.Split('/'))
            if (seg is "obj" or "bin") return true;
        return false;
    }

    /// <summary>Strips the repo root from an absolute tool path, yielding a forward-slash repo-relative path.</summary>
    private string ToRepoRelative(string? path)
    {
        string s = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (_repoRoot.Length > 0 && s.StartsWith(_repoRoot + "/", StringComparison.Ordinal))
            s = s.Substring(_repoRoot.Length + 1);
        return s.TrimStart('/');
    }
}
