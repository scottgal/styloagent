using System.Text.Json;
using Styloagent.Core.Ownership;

namespace Styloagent.Core.Hooks;

/// <summary>
/// Transport-agnostic bridge from a raw PreToolUse hook event to the deny decision a PreToolUse hook returns.
/// Reuses <see cref="HookEventParser"/> to pull the tool + target path out of the event JSON, runs the
/// <see cref="OwnershipGate"/> policy as the calling agent, and — for a blocked cross-owner write — formats
/// the Claude Code <c>hookSpecificOutput</c> deny payload. Whatever invokes it (a headless gate-mode of the
/// app, or a cockpit round-trip handler) just prints the returned string. Pure and never-throws: on ANY
/// error it returns null (allow), honouring the gate's fail-open contract.
/// </summary>
public static class OwnershipGateCli
{
    /// <summary>
    /// Evaluate a PreToolUse <paramref name="eventJson"/> for <paramref name="caller"/> against
    /// <paramref name="gate"/>. Returns the deny-decision JSON to emit on stdout, or null to allow.
    /// </summary>
    public static string? Evaluate(OwnershipGate gate, string? caller, string? eventJson)
    {
        try
        {
            if (gate is null || string.IsNullOrWhiteSpace(eventJson)) return null;
            if (!HookEventParser.TryParse(eventJson, caller ?? string.Empty, out HookEvent? evt) || evt is null)
                return null;   // unparseable event ⇒ allow (fail-open)

            GateDecision decision = gate.Decide(caller, evt.ToolName, evt.ToolTarget);
            return decision.IsAllowed ? null : DenyPayload(decision.Reason!);
        }
        catch
        {
            return null;   // never hard-block on an error
        }
    }

    /// <summary>
    /// Transport convenience: load the ownership map from <c>&lt;repoRoot&gt;/.styloagent/ownership.yaml</c>
    /// (missing/invalid ⇒ owns nothing ⇒ allow-all) and evaluate. Never-throws.
    /// </summary>
    public static string? Evaluate(string? caller, string? repoRoot, string? eventJson)
    {
        try
        {
            string root = repoRoot ?? string.Empty;
            var map = OwnershipMap.Load(Path.Combine(root, ".styloagent", "ownership.yaml"));
            return Evaluate(new OwnershipGate(map, root), caller, eventJson);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the Claude Code PreToolUse deny payload carrying the prod <paramref name="reason"/>.</summary>
    private static string DenyPayload(string reason)
    {
        // Serialize via the JSON writer so the reason is correctly escaped.
        var payload = new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = "deny",
                permissionDecisionReason = reason,
            },
        };
        return JsonSerializer.Serialize(payload);
    }
}
