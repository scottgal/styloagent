using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Issues;

/// <summary>
/// File-drop issue store under <c>.styloagent/issues/</c> (one markdown file per issue, same style as
/// the channel). Tolerant: unreadable files are skipped, never throws on read.
/// </summary>
public static partial class IssueStore
{
    [GeneratedRegex(@"^\*\*From:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex FromRx();
    [GeneratedRegex(@"^\*\*Timestamp:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TsRx();
    [GeneratedRegex(@"^\*\*Severity:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SevRx();
    [GeneratedRegex(@"^\*\*Status:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex StatusRx();
    [GeneratedRegex(@"^\*\*Source:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SourceRx();
    [GeneratedRegex(@"^#\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleRx();

    /// <summary>Writes a new open issue and returns it. Id is a slug of the title, de-duplicated.</summary>
    public static Issue Write(string issuesDir, string reporter, string title, string detail,
        string severity, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(issuesDir);

        var baseSlug = Slug(title);
        var id = baseSlug;
        var n = 1;
        while (File.Exists(Path.Combine(issuesDir, id + ".md")))
            id = $"{baseSlug}-{++n}";

        var issue = new Issue(id, title.Trim(), detail.Trim(),
            string.IsNullOrWhiteSpace(reporter) ? "unknown" : reporter.Trim(),
            timestamp, NormalizeSeverity(severity), "open", "internal");

        File.WriteAllText(Path.Combine(issuesDir, id + ".md"), Format(issue));
        return issue;
    }

    /// <summary>Reads all issues, newest first. Never throws.</summary>
    public static IReadOnlyList<Issue> Read(string issuesDir)
    {
        if (!Directory.Exists(issuesDir)) return Array.Empty<Issue>();

        var issues = new List<Issue>();
        string[] files;
        try { files = Directory.GetFiles(issuesDir, "*.md"); }
        catch { return Array.Empty<Issue>(); }

        foreach (var file in files)
        {
            try
            {
                var body = File.ReadAllText(file);
                var ts = DateTimeOffset.TryParse(Match(TsRx(), body), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed) ? parsed : DateTimeOffset.MinValue;
                issues.Add(new Issue(
                    Id: Path.GetFileNameWithoutExtension(file),
                    Title: Match(TitleRx(), body) ?? "(untitled)",
                    Detail: body,
                    Reporter: Match(FromRx(), body) ?? "unknown",
                    Timestamp: ts,
                    Severity: Match(SevRx(), body) ?? "medium",
                    Status: Match(StatusRx(), body) ?? "open",
                    Source: Match(SourceRx(), body) ?? "internal"));
            }
            catch { /* skip unreadable */ }
        }

        return issues.OrderByDescending(i => i.Timestamp).ToList();
    }

    internal static string Format(Issue i) =>
        $"**From:** {i.Reporter}\n" +
        $"**Timestamp:** {i.Timestamp.ToString("o", CultureInfo.InvariantCulture)}\n" +
        $"**Severity:** {i.Severity}\n" +
        $"**Status:** {i.Status}\n" +
        $"**Source:** {i.Source}\n\n" +
        $"# {i.Title}\n\n{i.Detail}\n";

    /// <summary>Coerces free-text severity to one of <c>low</c> / <c>medium</c> / <c>high</c>.</summary>
    public static string NormalizeSeverity(string severity) =>
        (severity?.Trim().ToLowerInvariant()) switch
        {
            "low" => "low",
            "high" or "critical" => "high",
            _ => "medium",
        };

    private static string? Match(Regex rx, string body)
    {
        var m = rx.Match(body);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? "issue" : slug;
    }
}
