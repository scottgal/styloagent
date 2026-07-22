using Styloagent.Core.Hooks;

namespace Styloagent.App.Dock;

/// <summary>Pure membership policy for the dynamic Active agents layout.</summary>
public static class ActiveAgentLayoutPolicy
{
    /// <summary>
    /// Keeps an idle agent visible for a short grace period so a turn finishing does not make its terminal
    /// disappear immediately. Working/waiting/new sessions are visible; exited sessions are not.
    /// </summary>
    public static bool ShouldShow(
        AgentHookState state,
        DateTimeOffset? lastActivityAt,
        DateTimeOffset now,
        TimeSpan idleRetention)
    {
        if (state == AgentHookState.Exited) return false;
        if (state != AgentHookState.Idle) return true;
        if (lastActivityAt is not { } last) return false;
        return now - last <= idleRetention;
    }
}
