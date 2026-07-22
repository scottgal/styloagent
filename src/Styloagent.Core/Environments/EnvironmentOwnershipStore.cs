using System.Globalization;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Environments;

/// <summary>Append-only ownership journal plus its deterministic effective-state projection.</summary>
public static partial class EnvironmentOwnershipStore
{
    [GeneratedRegex(@"^\*\*Environment:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex EnvRx();
    [GeneratedRegex(@"^\*\*Action:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex ActionRx();
    [GeneratedRegex(@"^\*\*Initiator:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex InitiatorRx();
    [GeneratedRegex(@"^\*\*FromOwner:\*\*\s*(.*)$", RegexOptions.Multiline)] private static partial Regex FromRx();
    [GeneratedRegex(@"^\*\*ToOwner:\*\*\s*(.*)$", RegexOptions.Multiline)] private static partial Regex ToRx();
    [GeneratedRegex(@"^\*\*Reason:\*\*\s*(.*)$", RegexOptions.Multiline)] private static partial Regex ReasonRx();
    [GeneratedRegex(@"^\*\*Force:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex ForceRx();
    [GeneratedRegex(@"^\*\*Timestamp:\*\*\s*(.+)$", RegexOptions.Multiline)] private static partial Regex TimestampRx();

    public static EnvironmentRegistryState Read(string root)
    {
        var controlOwner = EnvironmentRegistry.ReadControlOwner(root);
        var definitions = EnvironmentRegistry.Read(root);
        var events = ReadEvents(root);
        var states = new List<EnvironmentState>();
        foreach (var definition in definitions)
        {
            var history = events.Where(e => e.EnvironmentId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Timestamp).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var owner = definition.ConfiguredOwner;
            string? pending = null;
            foreach (var e in history)
            {
                switch (e.Action)
                {
                    case EnvironmentOwnershipAction.Assign:
                        owner = e.ToOwner ?? owner;
                        pending = null;
                        break;
                    case EnvironmentOwnershipAction.Offer:
                        pending = e.ToOwner;
                        break;
                    case EnvironmentOwnershipAction.Accept when pending is not null && e.Initiator == pending:
                        owner = pending;
                        pending = null;
                        break;
                    case EnvironmentOwnershipAction.Revoke:
                    case EnvironmentOwnershipAction.Return:
                        owner = e.ToOwner ?? definition.FallbackOwner;
                        pending = null;
                        break;
                }
            }
            states.Add(new EnvironmentState(definition, owner, pending, history));
        }
        return new EnvironmentRegistryState(controlOwner, states);
    }

    public static IReadOnlyList<EnvironmentOwnershipEvent> ReadEvents(string root)
    {
        var dir = EnvironmentRegistry.OwnershipDir(root);
        if (!Directory.Exists(dir)) return Array.Empty<EnvironmentOwnershipEvent>();
        var result = new List<EnvironmentOwnershipEvent>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.md"))
            {
                try
                {
                    var body = File.ReadAllText(path);
                    if (!Enum.TryParse<EnvironmentOwnershipAction>(Match(ActionRx(), body), true, out var action)) continue;
                    if (!DateTimeOffset.TryParse(Match(TimestampRx(), body), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) continue;
                    var environment = Match(EnvRx(), body);
                    var initiator = Match(InitiatorRx(), body);
                    if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(initiator)) continue;
                    result.Add(new EnvironmentOwnershipEvent(Path.GetFileNameWithoutExtension(path), environment, action,
                        initiator, EmptyToNull(Match(FromRx(), body)), EmptyToNull(Match(ToRx(), body)),
                        Match(ReasonRx(), body) ?? "", bool.TryParse(Match(ForceRx(), body), out var force) && force,
                        timestamp));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    public static EnvironmentOwnershipEvent Append(string root, string environmentId,
        EnvironmentOwnershipAction action, string initiator, string? fromOwner, string? toOwner,
        string reason, bool force, DateTimeOffset timestamp)
    {
        var dir = EnvironmentRegistry.OwnershipDir(root);
        Directory.CreateDirectory(dir);
        var id = $"{timestamp.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var e = new EnvironmentOwnershipEvent(id, environmentId, action, initiator, fromOwner, toOwner,
            reason?.Trim() ?? "", force, timestamp.ToUniversalTime());
        var body =
            $"**Environment:** {e.EnvironmentId}\n" +
            $"**Action:** {e.Action}\n" +
            $"**Initiator:** {e.Initiator}\n" +
            $"**FromOwner:** {e.FromOwner ?? ""}\n" +
            $"**ToOwner:** {e.ToOwner ?? ""}\n" +
            $"**Reason:** {e.Reason.Replace('\n', ' ')}\n" +
            $"**Force:** {e.Force.ToString().ToLowerInvariant()}\n" +
            $"**Timestamp:** {e.Timestamp.ToString("o", CultureInfo.InvariantCulture)}\n";
        File.WriteAllText(Path.Combine(dir, id + ".md"), body);
        return e;
    }

    private static string? Match(Regex regex, string body)
    {
        var match = regex.Match(body);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
