using System.Text.RegularExpressions;
using Styloagent.Core.Git;

namespace Styloagent.Git;

// Unified-diff line classification derived from SourceGit (MIT). See Styloagent.Git/THIRD-PARTY.md
/// <summary>Parses <c>git diff</c> unified output for one file into a <see cref="FileDiff"/>.</summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static FileDiff Parse(string path, string gitDiffText)
    {
        var lines = new List<DiffLine>();
        int added = 0, deleted = 0, oldLine = 0, newLine = 0;
        bool inHunk = false, isBinary = false;

        foreach (var raw in gitDiffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inHunk)
            {
                if (line.StartsWith("Binary files ", StringComparison.Ordinal)) { isBinary = true; continue; }
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    oldLine = int.Parse(m.Groups[1].Value);
                    newLine = int.Parse(m.Groups[2].Value);
                    lines.Add(new DiffLine(DiffLineKind.Header, line, 0, 0));
                    inHunk = true;
                }
                continue; // skip diff --git / index / --- / +++ preamble
            }

            var next = HunkHeader().Match(line);
            if (next.Success)
            {
                oldLine = int.Parse(next.Groups[1].Value);
                newLine = int.Parse(next.Groups[2].Value);
                lines.Add(new DiffLine(DiffLineKind.Header, line, 0, 0));
                continue;
            }
            if (line.Length == 0) continue;

            var prefix = line[0];
            var content = line[1..];
            switch (prefix)
            {
                case '-':
                    deleted++;
                    lines.Add(new DiffLine(DiffLineKind.Deleted, content, oldLine, 0));
                    oldLine++;
                    break;
                case '+':
                    added++;
                    lines.Add(new DiffLine(DiffLineKind.Added, content, 0, newLine));
                    newLine++;
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, content, oldLine, newLine));
                    oldLine++; newLine++;
                    break;
                case '\\': // "\ No newline at end of file" — ignore
                    break;
                default:
                    break;
            }
        }

        return new FileDiff(path, added, deleted, isBinary, lines);
    }
}
