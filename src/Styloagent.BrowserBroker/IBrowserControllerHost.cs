namespace Styloagent.BrowserBroker;

/// <summary>
/// The minimal host surface the broker needs from whichever client embeds it (the cockpit, a test rig,
/// an external project). EnvironmentsRoot is where environment ownership + allow-list rules live;
/// BrowserRoot is where jobs and artifacts are stored. NotifyBrowserRefresh fires after a run finishes
/// so the host can refresh its own view of browser state.
/// </summary>
public interface IBrowserControllerHost
{
    /// <summary>The project's <c>.styloagent/environments</c> root (rules), or null when none is active.</summary>
    string? EnvironmentsRoot { get; }

    /// <summary>The project's <c>.styloagent/browser</c> root (jobs + artifacts), or null when none is active.</summary>
    string? BrowserRoot { get; }

    /// <summary>Called (from a background thread) after a browser run completes so the host can refresh.</summary>
    void NotifyBrowserRefresh();
}
