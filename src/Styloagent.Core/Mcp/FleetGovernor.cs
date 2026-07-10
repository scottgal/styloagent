namespace Styloagent.Core.Mcp;

/// <summary>Pure fleet policy: decides whether a spawn is allowed. No I/O, no state.</summary>
public static class FleetGovernor
{
    public static Decision Check(FleetState state, string parentPrefix, string newPrefix)
    {
        if (state.Paused)
            return Decision.Deny(RejectReason.Paused, "fleet is paused");

        if (!IsValidPrefix(newPrefix))
            return Decision.Deny(RejectReason.InvalidPrefix,
                $"'{newPrefix}' is not a valid prefix (lowercase word ending in '-')");

        var parent = state.Members.FirstOrDefault(m => m.Prefix == parentPrefix);
        if (parent is null)
            return Decision.Deny(RejectReason.UnknownParent, $"unknown parent '{parentPrefix}'");

        if (state.Members.Any(m => m.Prefix == newPrefix))
            return Decision.Deny(RejectReason.DuplicatePrefix, $"'{newPrefix}' already exists");

        if (state.Members.Count >= state.MaxFleet)
            return Decision.Deny(RejectReason.FleetFull, $"fleet full ({state.Members.Count}/{state.MaxFleet})");

        int childDepth = parent.Depth + 1;
        if (childDepth > state.MaxDepth)
            return Decision.Deny(RejectReason.MaxDepth, $"max depth {state.MaxDepth} reached");

        return Decision.Allow();
    }

    // A prefix is a lowercase token of [a-z0-9-] ending in '-' (e.g. "foss-").
    private static bool IsValidPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.EndsWith('-') || prefix.Length < 2)
            return false;
        foreach (char c in prefix)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        return true;
    }
}
